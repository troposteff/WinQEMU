using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;
using WinQEMU.Text;
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
            DialogWidth.Fit(this, 680);

            _viewModel = viewModel;
            _ownerWindow = ownerWindow;
            DataContext = viewModel;

            // Listed now rather than bound: the outputs are read from Windows, and a headset may have been
            // plugged in since the last time.
            AudioOutputBox.ItemsSource = viewModel.AudioOutputChoices;
            AudioOutputBox.SelectedValue = viewModel.SelectedAudioOutputId;
            LanguageBox.ItemsSource = viewModel.LanguageChoices;
            LanguageBox.SelectedValue = viewModel.SelectedLanguage;
            ThemeBox.ItemsSource = viewModel.ThemeChoices;
            ThemeBox.SelectedValue = viewModel.SelectedTheme;
            StartAtLogonHint.Text = DialogText.SettingsStartAtLogonHint;

            _ = ReadStartAtLogonAsync();
        }

        // ---- Start at the Windows logon ----

        private const string StartupTaskId = "WinQEMUStartup";
        private bool _readingStartup;

        /// <summary>The package's startup task, as Windows has it: the user may have turned it off there.</summary>
        private async Task ReadStartAtLogonAsync()
        {
            _readingStartup = true;
            try
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                ShowStartupState(task.State);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                StartAtLogonSwitch.IsEnabled = false;
                StartAtLogonHint.Text = string.Format(CultureInfo.CurrentCulture, DialogText.SettingsStartupUnavailable, ex.Message);
            }
            finally
            {
                _readingStartup = false;
            }
        }

        private void ShowStartupState(Windows.ApplicationModel.StartupTaskState state)
        {
            _readingStartup = true;
            StartAtLogonSwitch.IsOn = state is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            _readingStartup = false;
            // Turned off in Windows' settings, or by policy: only Windows can turn it back on.
            if (state == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                StartAtLogonHint.Text = DialogText.SettingsStartupDisabledByUser;
            else if (state is Windows.ApplicationModel.StartupTaskState.DisabledByPolicy or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
                StartAtLogonHint.Text = DialogText.SettingsStartupByPolicy;
            StartAtLogonSwitch.IsEnabled = state is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.Disabled;
        }

        private async void OnStartAtLogonToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_readingStartup)
                return;
            try
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                if (StartAtLogonSwitch.IsOn)
                    ShowStartupState(await task.RequestEnableAsync());
                else
                {
                    task.Disable();
                    ShowStartupState(task.State);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                StartAtLogonHint.Text = string.Format(CultureInfo.CurrentCulture, DialogText.SettingsStartupRefused, ex.Message);
            }
        }

        // ---- Sound ----

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_refilling && ThemeBox.SelectedValue is string code)
            {
                _viewModel.SelectedTheme = code;
                // This dialog is already shown: it follows at once too.
                RequestedTheme = code switch
                {
                    "light" => Microsoft.UI.Xaml.ElementTheme.Light,
                    "dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
                    _ => Microsoft.UI.Xaml.ElementTheme.Default
                };
            }
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refilling || LanguageBox.SelectedValue is not string code || code == _viewModel.SelectedLanguage)
                return;
            _viewModel.SelectedLanguage = code;
            // This dialog is already shown: it follows at once too. Queued, as the list being refilled is the one whose
            // selection is changing.
            DispatcherQueue.TryEnqueue(ApplyLanguage);
        }

        private bool _refilling;

        /// <summary>Its own text read again (x:Bind, and the lists' words), the selections kept.</summary>
        private void ApplyLanguage()
        {
            _refilling = true;
            try
            {
                Bindings.Update();
                LanguageBox.ItemsSource = _viewModel.LanguageChoices;
                LanguageBox.SelectedValue = _viewModel.SelectedLanguage;
                ThemeBox.ItemsSource = _viewModel.ThemeChoices;
                ThemeBox.SelectedValue = _viewModel.SelectedTheme;
                AudioOutputBox.ItemsSource = _viewModel.AudioOutputChoices;
                AudioOutputBox.SelectedValue = _viewModel.SelectedAudioOutputId;
            }
            finally
            {
                _refilling = false;
            }
            StartAtLogonHint.Text = DialogText.SettingsStartAtLogonHint;
            _ = ReadStartAtLogonAsync();
        }

        private void OnAudioOutputChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_refilling && AudioOutputBox.SelectedValue is string id)
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

        /// <summary>Set by « Vérifier ce PC »: the window opens the assistant once this dialog is closed.</summary>
        public bool ReadinessRequested { get; private set; }

        private void OnReadinessClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            ReadinessRequested = true;
            Hide();
        }

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
