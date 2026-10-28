using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WinQEMU.Text;

namespace WinQEMU.Controls
{
    /// <summary>
    /// Takes files dragged from Explorer onto the console, whose window belongs to QEMU: registered on the
    /// <see cref="DropCatcher"/> laid over it while a drag is under way.
    /// </summary>
    /// <remarks>
    /// The drag image and its caption ("Copier sur le Bureau de l'invité") are drawn by Explorer, through IDropTargetHelper and a
    /// DROPDESCRIPTION set on the dragged data.
    /// </remarks>
    [ComVisible(true)]
    public sealed class ConsoleDropTarget : IDropTarget
    {
        private const int CfHdrop = 15;
        private const int DropEffectNone = 0;
        private const int DropEffectCopy = 1;
        private const int DropImageCopy = 1;
        private const int DropImageNoImage = -1;

        private readonly IntPtr _window;
        private readonly Func<bool> _canDrop;
        private readonly Action<IReadOnlyList<string>> _dropped;
        private readonly IDropTargetHelper? _helper;
        private bool _accepting;
        private IDataObject? _dragged;

        private ConsoleDropTarget(IntPtr window, Func<bool> canDrop, Action<IReadOnlyList<string>> dropped)
        {
            _window = window;
            _canDrop = canDrop;
            _dropped = dropped;
            try
            {
                _helper = (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"))!)!;
            }
            catch (COMException)
            {
                // Only the drag image is lost.
            }
        }

        /// <summary>
        /// Registers a target on <paramref name="window"/>, a window of this process, or explains why not (null
        /// target). Must run on the UI thread, which OLE needs initialised as a single-threaded apartment.
        /// </summary>
        /// <param name="canDrop">Whether a drop is taken now (a running machine, no copy under way).</param>
        public static ConsoleDropTarget? Register(IntPtr window, Func<bool> canDrop, Action<IReadOnlyList<string>> dropped,
            out string? problem)
        {
            var initialised = OleInitialize(IntPtr.Zero);
            if (initialised < 0)
            {
                problem = $"OleInitialize failed (0x{initialised:X8})";
                return null;
            }
            var target = new ConsoleDropTarget(window, canDrop, dropped);
            var registered = RegisterDragDrop(window, target);
            if (registered < 0)
            {
                problem = $"RegisterDragDrop failed (0x{registered:X8})";
                return null;
            }
            problem = null;
            return target;
        }

        public void Revoke() => RevokeDragDrop(_window);

        /// <summary>Diagnostics: whether drags reach this target, and whether it takes them.</summary>
        public Action<string>? Log { get; set; }

        /// <summary>The drag left the console or was dropped on it: the window carrying this target can go.</summary>
        public event Action? Ended;

        int IDropTarget.DragEnter(IDataObject data, int keyState, DropPoint point, ref int effect)
        {
            _dragged = data;
            var hasFiles = HasFiles(data);
            _accepting = hasFiles && _canDrop();
            Log?.Invoke($"Console: a drag entered the console (files: {hasFiles}, taken: {_accepting}).");
            effect = _accepting ? DropEffectCopy : DropEffectNone;
            if (_accepting)
                Describe(data, DropImageCopy, DialogText.DropCopyTo, DialogText.DropGuestDesktop);
            else
                Describe(data, DropImageNoImage, string.Empty, string.Empty);
            var screen = point;
            _helper?.DragEnter(_window, data, ref screen, effect);
            return 0;
        }

        int IDropTarget.DragOver(int keyState, DropPoint point, ref int effect)
        {
            effect = _accepting ? DropEffectCopy : DropEffectNone;
            var screen = point;
            _helper?.DragOver(ref screen, effect);
            return 0;
        }

        int IDropTarget.DragLeave()
        {
            // Explorer keeps the last caption it was given, now wrong over its own windows.
            if (_dragged is { } data)
                Describe(data, DropImageNoImage, string.Empty, string.Empty);
            _dragged = null;
            _helper?.DragLeave();
            Ended?.Invoke();
            return 0;
        }

        int IDropTarget.Drop(IDataObject data, int keyState, DropPoint point, ref int effect)
        {
            effect = _accepting ? DropEffectCopy : DropEffectNone;
            _dragged = null;
            var screen = point;
            _helper?.Drop(data, ref screen, effect);
            var files = _accepting ? ReadFiles(data) : new List<string>();
            Log?.Invoke($"Console: {files.Count} item(s) dropped on the console.");
            Ended?.Invoke();
            if (files.Count > 0)
                _dropped(files);
            return 0;
        }

        private static FORMATETC HdropFormat() => new()
        {
            cfFormat = CfHdrop,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };

        private static bool HasFiles(IDataObject data)
        {
            var format = HdropFormat();
            return data.QueryGetData(ref format) == 0;
        }

        private static List<string> ReadFiles(IDataObject data)
        {
            var files = new List<string>();
            var format = HdropFormat();
            data.GetData(ref format, out var medium);
            try
            {
                var count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFile(medium.unionmember, i, null, 0);
                    var name = new StringBuilder((int)length + 1);
                    DragQueryFile(medium.unionmember, i, name, (uint)name.Capacity);
                    files.Add(name.ToString());
                }
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
            return files;
        }

        /// <summary>
        /// The caption under the drag image, as Explorer writes "Copier vers Documents". Best effort: a source
        /// that does not take the format simply shows none.
        /// </summary>
        private static void Describe(IDataObject data, int image, string message, string insert)
        {
            var format = new FORMATETC
            {
                cfFormat = (short)RegisterClipboardFormat("DropDescription"),
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL
            };
            // DROPDESCRIPTION: an int, then two WCHAR[260].
            const int size = 4 + 2 * 260 * 2;
            var memory = GlobalAlloc(0x0042, (UIntPtr)size); // GHND: movable, zeroed
            if (memory == IntPtr.Zero)
                return;
            var pointer = GlobalLock(memory);
            Marshal.WriteInt32(pointer, image);
            WriteText(pointer + 4, message);
            WriteText(pointer + 4 + 520, insert);
            GlobalUnlock(memory);
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
            try
            {
                data.SetData(ref format, ref medium, true);
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException)
            {
                GlobalFree(memory);
            }
        }

        private static void WriteText(IntPtr destination, string text)
        {
            var chars = text.Length > 259 ? text[..259] : text;
            Marshal.Copy(chars.ToCharArray(), 0, destination, chars.Length);
        }

        [DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr reserved);

        [DllImport("ole32.dll")]
        private static extern int RegisterDragDrop(IntPtr window, IDropTarget target);

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr window);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint length);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DropPoint
    {
        public int X;
        public int Y;
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject data, int keyState, DropPoint point, ref int effect);
        [PreserveSig] int DragOver(int keyState, DropPoint point, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject data, int keyState, DropPoint point, ref int effect);
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr window, IDataObject data, ref DropPoint point, int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref DropPoint point, int effect);
        [PreserveSig] int Drop(IDataObject data, ref DropPoint point, int effect);
        [PreserveSig] int Show(bool show);
    }
}
