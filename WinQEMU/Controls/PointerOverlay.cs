using System;
using System.Runtime.InteropServices;

namespace WinQEMU.Controls
{
    /// <summary>
    /// A window of WinQEMU's own, laid over QEMU's console while the guest image is scaled, that takes
    /// the mouse so the shell can point for QEMU.
    /// </summary>
    /// <remarks>
    /// QEMU 11.1 cannot point in a scaled image: SDL converts the pointer into image coordinates and
    /// QEMU then scales them against the window instead of the image, so the guest pointer drifted
    /// further from the host's the further it went from the top-left corner. The shell knows both
    /// sizes and sends the right position over QMP instead, as it already does for the keyboard.
    ///
    /// Layered at alpha 1/255: invisible, yet still hit by the mouse (a fully transparent layered
    /// window is not). Its cursor is the plain arrow: the guest's cursor shapes only exist inside QEMU's
    /// window, which no longer sees the mouse. At the guest's own size QEMU points right, and this window
    /// is hidden.
    /// </remarks>
    internal sealed class PointerOverlay : IDisposable
    {
        private const string ClassName = "WinQEMU.PointerOverlay";

        private const int WmSetCursor = 0x0020;
        private const int WmMouseActivate = 0x0021;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonUp = 0x0208;
        private const int WmMouseWheel = 0x020A;
        private const int WmXButtonDown = 0x020B;
        private const int WmXButtonUp = 0x020C;
        private const int WmMouseHWheel = 0x020E;
        private const int WmCaptureChanged = 0x0215;
        private const int MaNoActivate = 3;

        private static WndProc? s_proc;
        private static bool s_registered;

        private readonly IntPtr _handle;
        private int _buttonsDown;

        /// <summary>The pointer moved to a point of this window's client area (physical pixels).</summary>
        public event Action<int, int>? Moved;

        /// <summary>A button went down or up: QMP's InputButton name, at a point of the client area.</summary>
        public event Action<string, bool, int, int>? Button;

        public IntPtr Handle => _handle;

        public bool IsVisible { get; private set; }

        public PointerOverlay(IntPtr parent)
        {
            if (!s_registered)
            {
                // Static: every overlay shares the class, and the delegate must outlive all of them.
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
            _handle = CreateWindowEx(0x00080000 | 0x08000000, ClassName, "WinQEMU pointer", 0x40000000 | 0x04000000,
                0, 0, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_handle == IntPtr.Zero)
                return;

            SetLayeredWindowAttributes(_handle, 0, 1, 0x2); // LWA_ALPHA
            Instances[_handle] = this;
        }

        public bool IsCreated => _handle != IntPtr.Zero;

        /// <summary>Covers a rectangle of the parent, above every other child, and shows the window.</summary>
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
            if (_buttonsDown != 0)
                ReleaseCapture();
        }

        /// <summary>Limits the window to a rectangle of its own client area (null: all of it), like the console's region.</summary>
        public void SetVisibleRegion((int Left, int Top, int Right, int Bottom)? visible)
        {
            if (_handle == IntPtr.Zero)
                return;
            if (visible is not { } rect)
            {
                SetWindowRgn(_handle, IntPtr.Zero, true);
                return;
            }
            var region = CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (region != IntPtr.Zero && SetWindowRgn(_handle, region, true) == 0)
                DeleteObject(region);
        }

        private static readonly System.Collections.Generic.Dictionary<IntPtr, PointerOverlay> Instances = new();

        private static IntPtr Dispatch(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam) =>
            Instances.TryGetValue(hWnd, out var overlay) && overlay.Process(message, wParam, lParam) is { } result
                ? result
                : DefWindowProc(hWnd, message, wParam, lParam);

        private IntPtr? Process(uint message, IntPtr wParam, IntPtr lParam)
        {
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            switch (message)
            {
                case WmMouseActivate:
                    // The keyboard stays with XAML, which forwards it; a click here must not take it.
                    return MaNoActivate;
                case WmMouseMove:
                    Moved?.Invoke(x, y);
                    return IntPtr.Zero;
                case WmLButtonDown or WmLButtonUp:
                    return Press("left", message == WmLButtonDown, x, y);
                case WmRButtonDown or WmRButtonUp:
                    return Press("right", message == WmRButtonDown, x, y);
                case WmMButtonDown or WmMButtonUp:
                    return Press("middle", message == WmMButtonDown, x, y);
                case WmXButtonDown or WmXButtonUp:
                    // XBUTTON1 is "back", which QEMU calls side; XBUTTON2 "forward", extra.
                    Press(((wParam.ToInt64() >> 16) & 0xFFFF) == 1 ? "side" : "extra", message == WmXButtonDown, x, y);
                    return (IntPtr)1;
                case WmMouseWheel or WmMouseHWheel:
                {
                    // Wheel positions are in screen coordinates; the last move already placed the pointer.
                    var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    var name = message == WmMouseWheel
                        ? delta > 0 ? "wheel-up" : "wheel-down"
                        : delta > 0 ? "wheel-right" : "wheel-left";
                    Button?.Invoke(name, true, int.MinValue, int.MinValue);
                    Button?.Invoke(name, false, int.MinValue, int.MinValue);
                    return IntPtr.Zero;
                }
                case WmCaptureChanged:
                    _buttonsDown = 0;
                    return IntPtr.Zero;
                default:
                    return null;
            }
        }

        private IntPtr Press(string button, bool down, int x, int y)
        {
            // Captured while a button is held, so a drag that leaves the image still ends in the guest.
            if (down && _buttonsDown++ == 0)
                SetCapture(_handle);
            else if (!down && _buttonsDown > 0 && --_buttonsDown == 0)
                ReleaseCapture();
            Button?.Invoke(button, down, x, y);
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero)
                return;
            Instances.Remove(_handle);
            DestroyWindow(_handle);
        }

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
        private static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, int name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
    }
}
