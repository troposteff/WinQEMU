using System.Runtime.InteropServices;
using System.Text;

namespace WinQEMU.Core.Interop
{
    /// <summary>
    /// Win32 window-management entry points used to embed QEMU's SDL console. These live in the core
    /// rather than the shell because locating and reparenting a window is not a XAML concern — only
    /// the layout tracking is.
    /// </summary>
    public static class NativeMethods
    {
        public const int GwlStyle = -16;
        public const int GwlExStyle = -20;

        public const long WsChild = 0x40000000L;
        public const long WsPopup = 0x80000000L;
        public const long WsCaption = 0x00C00000L;
        public const long WsThickFrame = 0x00040000L;
        public const long WsMinimizeBox = 0x00020000L;
        public const long WsMaximizeBox = 0x00010000L;
        public const long WsSysMenu = 0x00080000L;
        public const long WsBorder = 0x00800000L;
        public const long WsDlgFrame = 0x00400000L;
        public const long WsVisible = 0x10000000L;
        public const long WsClipSiblings = 0x04000000L;

        public const long WsExDlgModalFrame = 0x00000001L;
        public const long WsExClientEdge = 0x00000200L;
        public const long WsExWindowEdge = 0x00000100L;
        public const long WsExAppWindow = 0x00040000L;
        public const long WsExStaticEdge = 0x00020000L;

        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpFrameChanged = 0x0020;
        public const uint SwpShowWindow = 0x0040;
        public const uint SwpHideWindow = 0x0080;

        /// <summary>
        /// Posts the request instead of sending it when the caller and the window belong to
        /// different input queues. Essential for a cross-process child window: without it every
        /// reposition blocks our thread until QEMU's message loop gets around to answering.
        /// </summary>
        public const uint SwpAsyncWindowPos = 0x4000;

        /// <summary>HWND_TOP — places the window at the top of its sibling Z-order.</summary>
        public static readonly IntPtr HwndTop = IntPtr.Zero;

        public const int SwHide = 0;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        /// <summary>The pointer in physical screen pixels (the shell is per-monitor DPI aware).</summary>
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

        /// <summary>MapVirtualKey translation: virtual key to scancode, with 0xE0/0xE1 for extended keys.</summary>
        public const uint MapvkVkToVscEx = 4;

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);

        public const long WsExToolWindow = 0x00000080L;
        public const long WsExNoActivate = 0x08000000L;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int command);

        public const uint RdwInvalidate = 0x0001;
        public const uint RdwErase = 0x0004;
        public const uint RdwAllChildren = 0x0080;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();


        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        // GetWindowLongPtrW/SetWindowLongPtrW are only exported on 64-bit Windows; 32-bit builds
        // have to fall back to the non-Ptr variants.

        public static long GetWindowStyle(IntPtr hWnd, int index) =>
            IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, index).ToInt64()
                : GetWindowLong32(hWnd, index);

        public static void SetWindowStyle(IntPtr hWnd, int index, long value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hWnd, index, new IntPtr(value));
            else
                SetWindowLong32(hWnd, index, (int)value);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);
    }
}
