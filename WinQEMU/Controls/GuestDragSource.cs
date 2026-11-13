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
    /// drop still happens at once, where the button is released, and the target waits for the files: asked for them
    /// after the release, the data object answers once the copy has ended, from a thread of its own (FileDataObject).
    /// Explorer, which supports asynchronous drops, copies on its own thread meanwhile. The names are known from the
    /// start (the drop target reads them while the pointer passes over it); the files arrive later. Escape cancels.
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
        /// <param name="released">Called as soon as the button is released, before the target has the files.</param>
        public static bool Run(IntPtr window, IReadOnlyList<string> paths, Task copy, Action<string> log, Action released)
        {
            var data = new FileDataObject(paths, copy, log);
            var source = new DropSource(copy, log, data, released);
            // OLE's loop only asks the source again (QueryContinueDrag) when the mouse or the keyboard does something: a
            // release that came before the drag started would wait for the user to move the mouse (seen: 36 s). So a
            // mouse move where the pointer already is, every tenth of a second, posted to the window that holds the
            // mouse for OLE. An empty message to the main window did not wake it.
            var thread = GetCurrentThreadId();
            using var nudge = new Timer(_ => Nudge(thread), null, 100, 100);
            var proxy = ProxyFromMultithreadedApartment(data);
            int result, effect;
            try
            {
                result = SHDoDragDrop(window, proxy, source, DropEffectCopy, out effect);
            }
            finally
            {
                Marshal.Release(proxy);
            }
            log($"Drag out of the guest: SHDoDragDrop returned 0x{result:X8}, effect {effect}, copy {(copy.IsCompletedSuccessfully ? "done" : copy.IsCompleted ? "failed" : "unfinished")}.");
            if (result != DragDropSDrop)
                return false;
            // With the right button, Explorer returns before its menu ("Copy here", "Cancel") has been answered, with
            // no effect; the files must still be there when the answer comes. What the target said as the pointer
            // passed over it is all there is to go by: a release where nothing took files gives nothing.
            // And a target that was given the paths after the release has them, whatever effect it reports: some answer
            // "none" so that no source ever deletes what they took, and the folder was then removed under them.
            return (effect & DropEffectCopy) != 0 || (source.RightButton && (source.LastEffect & DropEffectCopy) != 0)
                || data.GivenAfterRelease;
        }

        /// <summary>
        /// The data object as seen from this thread: a proxy to it, marshalled from a thread of the multithreaded
        /// apartment, where its calls are then served (see <see cref="FileDataObject"/>).
        /// </summary>
        private static IntPtr ProxyFromMultithreadedApartment(FileDataObject data)
        {
            var iid = typeof(IDataObject).GUID;
            var stream = Task.Run(() =>
            {
                CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED; a thread-pool thread is already in the MTA
                var unknown = Marshal.GetComInterfaceForObject(data, typeof(IDataObject));
                try
                {
                    Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref iid, unknown, out var marshalled));
                    return marshalled;
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            }).GetAwaiter().GetResult();
            Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, ref iid, out var proxy));
            return proxy;
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
            private readonly FileDataObject _data;
            private readonly Action _onReleased;
            private int _lastEffect;
            private bool _released;

            public DropSource(Task copy, Action<string> log, FileDataObject data, Action onReleased)
            {
                _copy = copy;
                _log = log;
                _data = data;
                _onReleased = onReleased;
            }

            /// <summary>The effect the target last offered before the release.</summary>
            public int LastEffect => _lastEffect;

            /// <summary>Whether the drag was made with the right button.</summary>
            public bool RightButton { get; private set; }

            public int QueryContinueDrag(int escapePressed, uint keyState)
            {
                if (escapePressed != 0)
                    return DragDropSCancel;
                if (!_released && (keyState & (MkLButton | MkRButton)) != 0)
                {
                    RightButton |= (keyState & MkRButton) != 0;
                    return 0;
                }
                if (!_released)
                {
                    _log($"Drag out of the guest: released (effect {_lastEffect}, copy {(_copy.IsCompleted ? "ended" : "under way")}).");
                    // The guest's button too, now: the drop may take as long as the copy (the target waits for the
                    // files), and a pointer brought back over the console meanwhile carried the guest's drag along
                    // with it, still held, which then moved the dragged icon on the guest's desktop.
                    _onReleased();
                }
                _released = true;
                // Released where nothing takes files: no drop.
                if (_lastEffect == 0)
                    return DragDropSDrop;
                if (_copy.IsCompleted && !_copy.IsCompletedSuccessfully)
                    return DragDropSCancel;
                // Dropped at once, where the button was released, the copy under way or not: the target is given the
                // files once they are whole (FileDataObject). Answering "continue" until the copy ended, as before, let
                // OLE follow the mouse meanwhile, and the files landed wherever the pointer was when the copy ended.
                _data.MarkReleased();
                return DragDropSDrop;
            }

            public int GiveFeedback(int effect)
            {
                // Without DROPEFFECT_SCROLL (0x80000000), which Explorer adds over a list that scrolls: a target that
                // only scrolls takes nothing, and the release must not wait for the copy.
                if (!_released)
                    _lastEffect = effect & 0x7FFFFFFF;
                return DragDropSUseDefaultCursors;
            }
        }

        /// <summary>
        /// CF_HDROP of the given paths, and whatever the shell stores on it (the drag image, its feedback). Once the
        /// button is released, the paths are only given out when the copy from the guest has ended: the target then
        /// reads whole files.
        /// </summary>
        /// <remarks>
        /// That wait happens on a thread of the multithreaded apartment, never on the UI thread: the object is not agile
        /// (it refuses IMarshal) and is handed to OLE as a proxy marshalled from there (<see cref="ProxyFromMultithreadedApartment"/>),
        /// so every call, OLE's during the drag and the target's from its process, comes in on one of COM's threads.
        /// With <see cref="IDataObjectAsyncCapability"/>, Explorer takes the drop at once and copies on its own thread.
        /// </remarks>
        private sealed class FileDataObject : IDataObject, IDataObjectAsyncCapability, ICustomQueryInterface
        {
            private const short CfHDrop = 15;
            private static readonly Guid MarshalIid = new("00000003-0000-0000-C000-000000000046");
            private readonly IReadOnlyList<string> _paths;
            private readonly Task _copy;
            private readonly Action<string> _log;
            private readonly object _gate = new();
            private readonly List<(FORMATETC Format, STGMEDIUM Medium)> _stored = new();
            private volatile bool _released;
            private volatile bool _asyncMode = true;
            private volatile bool _inOperation;

            public FileDataObject(IReadOnlyList<string> paths, Task copy, Action<string> log)
            {
                _paths = paths;
                _copy = copy;
                _log = log;
            }

            /// <summary>The button was released over a target: from now on, the paths wait for the copy.</summary>
            public void MarkReleased() => _released = true;

            /// <summary>Whether a target was given the paths after the release: it then has the files to read.</summary>
            public bool GivenAfterRelease => _givenAfterRelease;

            private volatile bool _givenAfterRelease;

            /// <summary>
            /// What the shell stored on the object (its drag image is a 32-bit bitmap) was never freed, a leak with every
            /// drag. Freed with the object, not when the drag returns: the target may still hold it and ask.
            /// </summary>
            ~FileDataObject()
            {
                foreach (var (_, medium) in _stored)
                {
                    var owned = medium;
                    ReleaseStgMedium(ref owned);
                }
            }

            public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
            {
                ppv = IntPtr.Zero;
                return iid == MarshalIid ? CustomQueryInterfaceResult.Failed : CustomQueryInterfaceResult.NotHandled;
            }

            public void GetData(ref FORMATETC format, out STGMEDIUM medium)
            {
                if (format.cfFormat == CfHDrop && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
                {
                    if (_released && !_copy.IsCompleted)
                    {
                        _log("Drag out of the guest: the target asks for the files; they are given once the copy has ended.");
                        try
                        {
                            _copy.Wait();
                        }
                        catch (AggregateException)
                        {
                        }
                    }
                    if (_released && !_copy.IsCompletedSuccessfully)
                        throw new COMException("The copy from the guest failed.", unchecked((int)0x80004005)); // E_FAIL
                    medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = DropFiles(_paths) };
                    if (_released)
                        _givenAfterRelease = true;
                    return;
                }

                lock (_gate)
                {
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
                }
                throw new COMException("Format not offered.", unchecked((int)0x80040064)); // DV_E_FORMATETC
            }

            public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
                throw new NotImplementedException();

            public int QueryGetData(ref FORMATETC format)
            {
                if (format.cfFormat == CfHDrop && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
                    return 0;
                lock (_gate)
                {
                    foreach (var (stored, _) in _stored)
                    {
                        if (stored.cfFormat == format.cfFormat && (stored.tymed & format.tymed) != 0)
                            return 0;
                    }
                }
                return unchecked((int)0x80040064);
            }

            public int SetAsyncMode(int doOpAsync)
            {
                _asyncMode = doOpAsync != 0;
                return 0;
            }

            public int GetAsyncMode(out int isOpAsync)
            {
                isOpAsync = _asyncMode ? 1 : 0;
                return 0;
            }

            public int StartOperation(IntPtr bindContext)
            {
                _inOperation = true;
                _log("Drag out of the guest: the target copies on its own thread.");
                return 0;
            }

            public int InOperation(out int inAsyncOp)
            {
                inAsyncOp = _inOperation ? 1 : 0;
                return 0;
            }

            public int EndOperation(int result, IntPtr bindContext, uint effects)
            {
                _inOperation = false;
                _log($"Drag out of the guest: the target's copy ended (0x{result:X8}, effect {effects}).");
                return 0;
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
                lock (_gate)
                {
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
            }

            public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
            {
                if (direction != DATADIR.DATADIR_GET)
                    throw new COMException("Only GET.", unchecked((int)0x80004001)); // E_NOTIMPL
                var formats = new List<FORMATETC>
                {
                    new() { cfFormat = CfHDrop, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL }
                };
                lock (_gate)
                {
                    foreach (var (stored, _) in _stored)
                        formats.Add(stored);
                }
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

        [ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataObjectAsyncCapability
        {
            [PreserveSig]
            int SetAsyncMode(int doOpAsync);

            [PreserveSig]
            int GetAsyncMode(out int isOpAsync);

            [PreserveSig]
            int StartOperation(IntPtr bindContext);

            [PreserveSig]
            int InOperation(out int inAsyncOp);

            [PreserveSig]
            int EndOperation(int result, IntPtr bindContext, uint effects);
        }

        [DllImport("shell32.dll")]
        private static extern int SHDoDragDrop(IntPtr window, IntPtr data, IDropSource source, int okEffects, out int effect);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint flags);

        [DllImport("ole32.dll")]
        private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid iid, IntPtr unknown, out IntPtr stream);

        [DllImport("ole32.dll")]
        private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid iid, out IntPtr unknown);

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
