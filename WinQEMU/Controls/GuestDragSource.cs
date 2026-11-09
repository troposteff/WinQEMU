using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace WinQEMU.Controls
{
    /// <summary>
    /// Files dragged out of the guest, as a drag of this PC: Explorer, or any other drop target, receives them as files
    /// (CF_HDROP) from a folder of WinQEMU's own where the copy from the guest is written.
    /// </summary>
    /// <remarks>
    /// The copy starts as soon as the drag leaves the console and usually ends before the drop. When it has not, the
    /// release of the button is held back (QueryContinueDrag goes on answering "continue") until it has, as 7-Zip does
    /// for an archive dragged out: the target only reads the files once they are whole, and OLE's drag loop keeps
    /// pumping messages meanwhile, so the copy, which runs on this thread's dispatcher, goes on. Escape cancels it.
    /// The names are known from the start (the drop target reads them while the pointer passes over it); the files
    /// arrive later.
    /// </remarks>
    internal static class GuestDragSource
    {
        private const int DragDropSDrop = 0x00040100;
        private const int DragDropSCancel = 0x00040101;
        private const int DragDropSUseDefaultCursors = 0x00040102;
        private const int DropEffectCopy = 1;
        private const uint MkLButton = 0x0001;
        private const uint MkRButton = 0x0002;

        /// <summary>
        /// Runs the drag, from now until the drop or a cancel; the button must be held. Returns whether the files were
        /// dropped on a target that took them.
        /// </summary>
        /// <param name="paths">The items as they will be on this PC, in WinQEMU's folder.</param>
        /// <param name="copy">The copy from the guest, under way: the drop waits for it.</param>
        public static bool Run(IntPtr window, IReadOnlyList<string> paths, Task copy, Action<string> log)
        {
            var data = new FileDataObject(paths);
            var source = new DropSource(copy, log);
            // OLE's loop only asks the source again (QueryContinueDrag) when the mouse or the keyboard does something:
            // a release that came before the drag started, or a drop waiting for the copy, would wait for the user to
            // move the mouse (seen: 36 s). So a mouse move where the pointer already is, every tenth of a second, posted
            // to the window that holds the mouse for OLE. An empty message to the main window did not wake it.
            var thread = GetCurrentThreadId();
            using var nudge = new Timer(_ => Nudge(thread), null, 100, 100);
            var result = SHDoDragDrop(window, data, source, DropEffectCopy, out var effect);
            log($"Drag out of the guest: SHDoDragDrop returned 0x{result:X8}, effect {effect}, copy {(copy.IsCompletedSuccessfully ? "done" : copy.IsCompleted ? "failed" : "unfinished")}.");
            return result == DragDropSDrop && (effect & DropEffectCopy) != 0;
        }

        private static void Nudge(uint thread)
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info) || info.Capture == IntPtr.Zero || !GetCursorPos(out var point))
                return;
            ScreenToClient(info.Capture, ref point);
            var keys = (GetAsyncKeyState(0x01) < 0 ? MkLButton : 0) | (GetAsyncKeyState(0x02) < 0 ? MkRButton : 0);
            PostMessage(info.Capture, 0x0200, (IntPtr)keys, (IntPtr)((point.Y << 16) | (point.X & 0xFFFF))); // WM_MOUSEMOVE
        }

        private sealed class DropSource : IDropSource
        {
            private readonly Task _copy;
            private readonly Action<string> _log;
            private int _lastEffect;
            private bool _released;

            public DropSource(Task copy, Action<string> log)
            {
                _copy = copy;
                _log = log;
            }

            public int QueryContinueDrag(int escapePressed, uint keyState)
            {
                if (escapePressed != 0)
                    return DragDropSCancel;
                if (!_released && (keyState & (MkLButton | MkRButton)) != 0)
                    return 0;
                if (!_released)
                    _log($"Drag out of the guest: released (effect {_lastEffect}, copy {(_copy.IsCompleted ? "ended" : "under way")}).");
                _released = true;
                // Released where nothing takes files: no drop, no need to wait.
                if (_lastEffect == 0)
                    return DragDropSDrop;
                if (!_copy.IsCompleted)
                    return 0;
                return _copy.IsCompletedSuccessfully ? DragDropSDrop : DragDropSCancel;
            }

            public int GiveFeedback(int effect)
            {
                if (!_released)
                    _lastEffect = effect;
                return DragDropSUseDefaultCursors;
            }
        }

        /// <summary>CF_HDROP of the given paths, and whatever the shell stores on it (the drag image, its feedback).</summary>
        private sealed class FileDataObject : IDataObject
        {
            private const short CfHDrop = 15;
            private readonly IReadOnlyList<string> _paths;
            private readonly List<(FORMATETC Format, STGMEDIUM Medium)> _stored = new();

            public FileDataObject(IReadOnlyList<string> paths) => _paths = paths;

            public void GetData(ref FORMATETC format, out STGMEDIUM medium)
            {
                if (format.cfFormat == CfHDrop && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
                {
                    medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = DropFiles(_paths) };
                    return;
                }

                foreach (var (stored, storedMedium) in _stored)
                {
                    if (stored.cfFormat == format.cfFormat && (stored.tymed & format.tymed) != 0)
                    {
                        medium = new STGMEDIUM
                        {
                            tymed = storedMedium.tymed,
                            unionmember = OleDuplicateData(storedMedium.unionmember, format.cfFormat, 0)
                        };
                        return;
                    }
                }
                throw new COMException("Format not offered.", unchecked((int)0x80040064)); // DV_E_FORMATETC
            }

            public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
                throw new NotImplementedException();

            public int QueryGetData(ref FORMATETC format)
            {
                if (format.cfFormat == CfHDrop && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
                    return 0;
                foreach (var (stored, _) in _stored)
                {
                    if (stored.cfFormat == format.cfFormat && (stored.tymed & format.tymed) != 0)
                        return 0;
                }
                return unchecked((int)0x80040064);
            }

            public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
            {
                formatOut = formatIn;
                formatOut.ptd = IntPtr.Zero;
                return 0x00040130; // DATA_S_SAMEFORMATETC
            }

            public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
            {
                // Only memory: the drag image helper's formats are HGLOBALs.
                if (medium.tymed != TYMED.TYMED_HGLOBAL)
                    throw new COMException("Only HGLOBAL is kept.", unchecked((int)0x80040069)); // DV_E_TYMED
                var kept = medium;
                if (!release)
                    kept.unionmember = OleDuplicateData(medium.unionmember, format.cfFormat, 0);
                for (var i = 0; i < _stored.Count; i++)
                {
                    if (_stored[i].Format.cfFormat == format.cfFormat)
                    {
                        var old = _stored[i].Medium;
                        ReleaseStgMedium(ref old);
                        _stored[i] = (format, kept);
                        return;
                    }
                }
                _stored.Add((format, kept));
            }

            public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
            {
                if (direction != DATADIR.DATADIR_GET)
                    throw new COMException("Only GET.", unchecked((int)0x80004001)); // E_NOTIMPL
                var formats = new List<FORMATETC>
                {
                    new() { cfFormat = CfHDrop, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL }
                };
                foreach (var (stored, _) in _stored)
                    formats.Add(stored);
                Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)formats.Count, formats.ToArray(), out var enumerator));
                return enumerator;
            }

            public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection)
            {
                connection = 0;
                return unchecked((int)0x80040003); // OLE_E_ADVISENOTSUPPORTED
            }

            public void DUnadvise(int connection) => throw new COMException(null, unchecked((int)0x80040003));

            public int EnumDAdvise(out IEnumSTATDATA? enumerator)
            {
                enumerator = null;
                return unchecked((int)0x80040003);
            }

            /// <summary>A DROPFILES block: header, then the paths, each ending in a NUL, and a final NUL.</summary>
            private static IntPtr DropFiles(IReadOnlyList<string> paths)
            {
                const int header = 20;
                var text = string.Join('\0', paths) + "\0\0";
                var size = header + text.Length * 2;
                var memory = GlobalAlloc(0x0042, (UIntPtr)size); // GHND: movable, zeroed
                var pointer = GlobalLock(memory);
                try
                {
                    Marshal.WriteInt32(pointer, 0, header); // pFiles
                    Marshal.WriteInt32(pointer, 16, 1);     // fWide
                    Marshal.Copy(text.ToCharArray(), 0, pointer + header, text.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }
                return memory;
            }
        }

        [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropSource
        {
            [PreserveSig]
            int QueryContinueDrag(int escapePressed, uint keyState);

            [PreserveSig]
            int GiveFeedback(int effect);
        }

        [DllImport("shell32.dll")]
        private static extern int SHDoDragDrop(IntPtr window, IDataObject data, IDropSource source, int okEffects, out int effect);

        [DllImport("shell32.dll")]
        private static extern int SHCreateStdEnumFmtEtc(uint count, [In] FORMATETC[] formats, out IEnumFORMATETC enumerator);

        [DllImport("ole32.dll")]
        private static extern IntPtr OleDuplicateData(IntPtr source, short format, uint flags);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr Caret;
            public int CaretLeft, CaretTop, CaretRight, CaretBottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr window, ref Point point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
