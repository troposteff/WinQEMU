using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using WinQEMU.Core.Services;

namespace WinQEMU
{
    /// <summary>
    /// The window opens where it was closed: same screen, position and size, maximized again if it was. Kept in the
    /// settings (AppSettings.Window), through GetWindowPlacement / SetWindowPlacement, whose restored rectangle stays
    /// right while the window is maximized, minimized or hidden in the notification area.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _maximizeWhenShown;

        /// <summary>
        /// Called before the window is first shown. A place on a screen that is no longer there is ignored (Windows then
        /// places the window), rather than opening it out of sight.
        /// </summary>
        private void RestorePlacement()
        {
            if (_viewModel.Settings.Window is not { } place
                || place.Right - place.Left < 200 || place.Bottom - place.Top < 150)
                return;

            var rect = new Win32Rect { Left = place.Left, Top = place.Top, Right = place.Right, Bottom = place.Bottom };
            if (MonitorFromRect(ref rect, MonitorDefaultToNull) == IntPtr.Zero)
                return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(hwnd, ref placement))
                return;
            placement.Flags = 0;
            placement.ShowCmd = SwHide; // still hidden: Activate or ShowFromBackground shows it
            placement.Normal = rect;
            // Twice: moved onto a screen of another scale, the window is resized by the DPI change after the first
            // call, and would open larger or smaller than it closed.
            SetWindowPlacement(hwnd, ref placement);
            SetWindowPlacement(hwnd, ref placement);
            _maximizeWhenShown = place.Maximized;
        }

        /// <summary>Shows the window at start, where it was closed.</summary>
        public void Open()
        {
            ApplyPendingMaximize();
            Activate();
        }

        /// <summary>Maximized at close: maximized again the first time the window is shown, over its restored place.</summary>
        private void ApplyPendingMaximize()
        {
            if (!_maximizeWhenShown)
                return;
            _maximizeWhenShown = false;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }

        /// <summary>
        /// Notes where the window is, in memory. Only with the standard presenter: in full screen the rectangle is the
        /// screen's, so the place is taken on the way in (SetConsoleFullScreen) and kept.
        /// </summary>
        private void RememberPlacement()
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(hwnd, ref placement))
                return;
            var maximized = presenter.State == OverlappedPresenterState.Maximized
                || (presenter.State == OverlappedPresenterState.Minimized && (placement.Flags & WpfRestoreToMaximized) != 0);
            var normal = placement.Normal;
            _viewModel.Settings.Window = new WindowPlace(normal.Left, normal.Top, normal.Right, normal.Bottom, maximized);
        }

        /// <summary>At close, whatever the way (button, notification area, Windows ending the session).</summary>
        private void SavePlacement()
        {
            RememberPlacement();
            try
            {
                _viewModel.Settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only the window's place is lost: it opens where Windows puts it.
            }
        }

        private const int SwHide = 0;
        private const int WpfRestoreToMaximized = 2;
        private const uint MonitorDefaultToNull = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Placement
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public Win32Point Minimized;
            public Win32Point Maximized;
            public Win32Rect Normal;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref Win32Rect rect, uint flags);
    }
}
