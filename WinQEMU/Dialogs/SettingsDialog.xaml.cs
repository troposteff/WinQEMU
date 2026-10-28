using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;
using WinQEMU.ViewModels;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Application settings: where QEMU lives and where virtual machines are kept.
    /// </summary>
    /// <remarks>
    /// There is no OK/Cancel pair. Both settings are applied as soon as they are picked, because both
    /// have visible consequences the user needs to see before deciding they were right: choosing a
    /// QEMU folder re-runs detection, and choosing a library folder rescans it. A deferred Apply would
    /// only report success or failure after the dialog had gone.
    /// </remarks>
    public sealed partial class SettingsDialog : ContentDialog
    {
        private readonly MainViewModel _viewModel;
        private readonly IntPtr _ownerWindow;

        public SettingsDialog(MainViewModel viewModel, IntPtr ownerWindow)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _ownerWindow = ownerWindow;
            DataContext = viewModel;

            // Listed now rather than bound: the outputs are read from Windows, and a headset may have been
            // plugged in since the last time.
            AudioOutputBox.ItemsSource = viewModel.AudioOutputChoices;
            AudioOutputBox.SelectedValue = viewModel.SelectedAudioOutputId;
        }

        // ---- Sound ----

        private void OnAudioOutputChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AudioOutputBox.SelectedValue is string id)
                _viewModel.SelectedAudioOutputId = id;
        }

        // ---- QEMU ----

        private async void OnBrowseQemuClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is null)
                return;

            // A bad folder is not reported in a nested dialog — a second ContentDialog cannot open
            // while this one is showing. The detection panel and the error bar below it say what
            // happened, which is more useful anyway since the offending path stays on screen.
            await RunAsync(() => _viewModel.SetQemuDirectoryAsync(folder));
        }

        private async void OnResetQemuClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
            await RunAsync(() => _viewModel.ClearQemuDirectoryAsync());

        private async void OnRedetectClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
            await RunAsync(() => _viewModel.InitializeAsync());

        // ---- Library ----

        private async void OnBrowseLibraryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is not null)
                _viewModel.SetLibraryRoot(folder);
        }

        private async void OnOpenLibraryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var path = _viewModel.LibraryRootDisplay;

            // The default library folder is created lazily on first use, so it may not exist yet.
            Directory.CreateDirectory(path);
            await Launcher.LaunchFolderPathAsync(path);
        }

        // ---- Plumbing ----

        private void OnCloseClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Hide();

        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // With no built-in buttons, Escape would otherwise do nothing.
            if (e.Key != Windows.System.VirtualKey.Escape)
                return;

            e.Handled = true;
            Hide();
        }

        /// <summary>
        /// Runs a settings change with the controls disabled, since detection probes the filesystem
        /// and runs qemu-system --version and can take a couple of seconds.
        /// </summary>
        private async Task RunAsync(Func<Task> action)
        {
            SettingsPanel.IsEnabled = false;
            BusyRing.IsActive = true;
            try
            {
                await action();
            }
            finally
            {
                BusyRing.IsActive = false;
                SettingsPanel.IsEnabled = true;
            }
        }

        private async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            // Pickers need an owner window; without it they throw in a packaged desktop app.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
