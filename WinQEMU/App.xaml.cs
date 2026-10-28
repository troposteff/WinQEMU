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
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            // Started at the Windows logon (the package's startup task): straight to the notification area,
            // where the machines marked to start with WinQEMU come up behind the scenes.
            if (Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind
                == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
                _window.StartInBackground();
            else
                _window.Activate();
        }

        /// <summary>WinQEMU was started again while running: its window comes back. Called on any thread.</summary>
        public static void ShowMainWindow()
        {
            var window = _window;
            window?.DispatcherQueue.TryEnqueue(window.ShowFromBackground);
        }
    }
}
