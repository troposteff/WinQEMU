using System;
using System.Runtime.InteropServices;

namespace WinQEMU.Controls
{
    /// <summary>
    /// A window of WinQEMU's own, laid over the console only while files are being dragged over it, which
    /// carries the drop target (<see cref="ConsoleDropTarget"/>).
    /// </summary>
    /// <remarks>
    /// The console is QEMU's window, and a drop target can only be registered on a window of one's own
    /// process. One registered on the shell window, the console's parent, was never called: OLE asks the
    /// window under the pointer and does not go on to its parent. VMware's tools solve the same problem with
    /// a "detection window" shown while a drag is under way; this is the same idea. Invisible (layered at
    /// alpha 1/255, which the mouse still hits), and hidden again as soon as the button is released, so it
    /// never stands between the user and the guest otherwise.
    /// </remarks>
    internal sealed class DropCatcher : IDisposable
    {
        private const string ClassName = "WinQEMU.DropCatcher";
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private static WndProc? s_proc;
        private static bool s_registered;

        private readonly IntPtr _handle;

        public DropCatcher(IntPtr parent)
        {
            if (!s_registered)
            {
                s_proc = Dispatch;
                var windowClass = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    WndProc = s_proc,
                    Instance = GetModuleHandle(null),
                    Cursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
                    ClassName = ClassName
                };
                s_registered = RegisterClassEx(ref windowClass) != 0;
            }

            // WS_CHILD | WS_CLIPSIBLINGS; WS_EX_LAYERED | WS_EX_NOACTIVATE.
            _handle = CreateWindowEx(0x00080000 | 0x08000000, ClassName, "WinQEMU drop", 0x40000000 | 0x04000000,
                0, 0, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_handle != IntPtr.Zero)
                SetLayeredWindowAttributes(_handle, 0, 1, 0x2); // LWA_ALPHA
        }

        public IntPtr Handle => _handle;

        public bool IsCreated => _handle != IntPtr.Zero;

        public bool IsVisible { get; private set; }

        /// <summary>Covers a rectangle of the parent's client area, above the console and the pointer overlay.</summary>
        public void Show(int x, int y, int width, int height)
        {
            if (_handle == IntPtr.Zero)
                return;
            // HWND_TOP, SWP_NOACTIVATE | SWP_SHOWWINDOW.
            SetWindowPos(_handle, IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height), 0x0010 | 0x0040);
            IsVisible = true;
        }

        public void Hide()
        {
            if (_handle == IntPtr.Zero || !IsVisible)
                return;
            // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW.
            SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0080);
            IsVisible = false;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                DestroyWindow(_handle);
        }

        private static IntPtr Dispatch(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam) =>
            message == WmMouseActivate ? MaNoActivate : DefWindowProc(hWnd, message, wParam, lParam);

        private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public WndProc WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, int name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);
    }
}
