using System;
using System.Runtime.InteropServices;

namespace WinQEMU.Controls
{
    /// <summary>
    /// Reports clicks on the shell window's child windows, QEMU's console among them. A click there goes
    /// to QEMU's process, so XAML never hears of it and its keyboard focus stays wherever it was — on the
    /// scale-mode box just used, say, which then took the keys meant for the guest: the user could not
    /// type a name into Windows' setup. Windows tells the parent of a click on a child window with
    /// WM_PARENTNOTIFY, even when the child belongs to another process.
    /// </summary>
    internal sealed class ChildClickWatcher : IDisposable
    {
        private const uint WmParentNotify = 0x0210;
        private const int WmLButtonDown = 0x0201;
        private const int WmRButtonDown = 0x0204;
        private const int WmMButtonDown = 0x0207;
        private const int WmXButtonDown = 0x020B;

        private readonly IntPtr _window;
        private readonly SubclassProc _proc;
        private readonly UIntPtr _id = new(0x57514D55);
        private bool _installed;

        /// <param name="clicked">The click's position, in the window's client coordinates (physical pixels).</param>
        public ChildClickWatcher(IntPtr window, Action<int, int> clicked)
        {
            _window = window;
            // Kept in a field: the delegate must outlive the subclass, or the next message calls freed code.
            _proc = (hWnd, message, wParam, lParam, id, data) =>
            {
                if (message == WmParentNotify
                    && (int)(wParam.ToInt64() & 0xFFFF) is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
                {
                    var point = lParam.ToInt64();
                    clicked((short)(point & 0xFFFF), (short)((point >> 16) & 0xFFFF));
                }
                return DefSubclassProc(hWnd, message, wParam, lParam);
            };
            _installed = SetWindowSubclass(window, _proc, _id, IntPtr.Zero);
        }

        public bool IsInstalled => _installed;

        public void Dispose()
        {
            if (_installed)
                RemoveWindowSubclass(_window, _proc, _id);
            _installed = false;
        }

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr data);

        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
