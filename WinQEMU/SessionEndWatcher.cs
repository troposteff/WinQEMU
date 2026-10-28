using System;
using System.Runtime.InteropServices;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>
    /// Holds Windows' shutdown, restart or sign-out while machines run, so their guests can be stopped
    /// properly first, as VMware Workstation does; without it Windows ended QEMU with WinQEMU and every guest
    /// lost its power, in the background as much as with the window open.
    /// </summary>
    /// <remarks>
    /// Windows asks every top-level window first (WM_QUERYENDSESSION), the hidden one of WinQEMU in the
    /// background too. Refusing with a reason (ShutdownBlockReasonCreate) puts WinQEMU and that reason on
    /// Windows' "these apps are preventing shutdown" screen, where the user can still choose to shut down
    /// anyway; WinQEMU quits once its machines are stopped, and Windows carries on. The answer must come at
    /// once: the stopping itself is left to <c>ending</c>, queued on the UI thread.
    /// </remarks>
    internal sealed class SessionEndWatcher : IDisposable
    {
        private const uint WmQueryEndSession = 0x0011;
        private const uint WmEndSession = 0x0016;

        private readonly IntPtr _window;
        private readonly SubclassProc _proc;
        private readonly UIntPtr _id = new(0x57514D56);
        private bool _installed;
        private bool _blocking;

        /// <param name="mustStop">Whether machines are running, asked when Windows wants to end the session.</param>
        /// <param name="ending">Windows wants to end the session and was told to wait.</param>
        /// <param name="ended">Windows' decision: true, the session ends now; false, the user cancelled.</param>
        public SessionEndWatcher(IntPtr window, Func<bool> mustStop, Action ending, Action<bool> ended)
        {
            _window = window;
            // Kept in a field: the delegate must outlive the subclass, or the next message calls freed code.
            _proc = (hWnd, message, wParam, lParam, id, data) =>
            {
                if (message == WmQueryEndSession && mustStop())
                {
                    SetReason(DialogText.SessionEndReason);
                    ending();
                    return IntPtr.Zero; // FALSE: not yet.
                }
                if (message == WmEndSession)
                    ended(wParam != IntPtr.Zero);
                return DefSubclassProc(hWnd, message, wParam, lParam);
            };
            _installed = SetWindowSubclass(window, _proc, _id, IntPtr.Zero);
        }

        public bool IsInstalled => _installed;

        /// <summary>The text Windows shows under WinQEMU on its shutdown screen.</summary>
        public void SetReason(string reason)
        {
            _blocking = ShutdownBlockReasonCreate(_window, reason) || _blocking;
        }

        /// <summary>Lets Windows end the session without waiting for WinQEMU any more.</summary>
        public void ClearReason()
        {
            if (_blocking)
                ShutdownBlockReasonDestroy(_window);
            _blocking = false;
        }

        public void Dispose()
        {
            ClearReason();
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

        [DllImport("user32.dll")]
        private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    }
}
