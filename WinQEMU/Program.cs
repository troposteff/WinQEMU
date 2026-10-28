using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WinQEMU
{
    /// <summary>
    /// One WinQEMU at a time: started again (from the Start menu, at logon) while one runs, it hands the
    /// activation to that one, which shows its window, and ends.
    /// </summary>
    /// <remarks>
    /// Needed since the window can be closed with machines left running in the background: a second
    /// WinQEMU would list the same machines without seeing that they run, and could start one twice.
    /// </remarks>
    public static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var main = AppInstance.FindOrRegisterForKey("WinQEMU");
            if (!main.IsCurrent)
            {
                // The running one may bring its window forward (Windows otherwise forbids it to a
                // process that did not receive the user's input). Redirected from another thread: the
                // call needs one not blocked by this wait.
                AllowSetForegroundWindow(main.ProcessId);
                Task.Run(async () => await main.RedirectActivationToAsync(activation)).Wait(TimeSpan.FromSeconds(10));
                return 0;
            }

            main.Activated += (_, _) => App.ShowMainWindow();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App();
            });
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }
}
