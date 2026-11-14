using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinQEMU
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static MainWindow? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// The last resort for an exception no handler caught (an event handler is "async void": what escapes it reaches
        /// here). Without this WinQEMU died, and its machines with it: QEMU is killed with the process that started it, a
        /// power cut for every guest, over a failed button. The exception is shown in the error bar and written to
        /// crash.log beside the settings; WinQEMU and the machines stay.
        /// </summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            try
            {
                var folder = WinQEMU.Core.Services.AppSettings.SettingsDirectory;
                Directory.CreateDirectory(folder);
                var log = System.IO.Path.Combine(folder, "crash.log");
                // Kept small: the last ones are what matters.
                if (File.Exists(log) && new FileInfo(log).Length > 512 * 1024)
                    File.Delete(log);
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to write it: still shown below.
            }
            try
            {
                _window?.ReportUnhandled(e.Exception);
            }
            catch (Exception)
            {
                // The window is what failed: nothing more to do than stay alive.
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            // Started at the Windows logon (the package's startup task): straight to the notification area,
            // where the machines marked to start with WinQEMU come up behind the scenes. Started by the winqemu
            // command too ("--background"): its window would otherwise jump in front of the terminal.
            // The argument is read from the activation, not from args: WinUI gave an empty Arguments there, and
            // the window opened in front of the terminal.
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask
                || (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                    && launch.Arguments.Split(' ').Contains("--background")))
                _window.StartInBackground();
            else
                _window.Open();
        }

        /// <summary>WinQEMU was started again while running: its window comes back. Called on any thread.</summary>
        public static void ShowMainWindow()
        {
            var window = _window;
            window?.DispatcherQueue.TryEnqueue(window.ShowFromBackground);
        }
    }
}
