using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinQEMU.ViewModels;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Whether this PC can run machines (ViewModels.MainViewModel.Readiness): QEMU and the hardware acceleration, each with
    /// what to do about it. Nothing is installed without the user's action: QEMU is theirs to install (the winget command is
    /// only copied), and enabling Windows Hypervisor Platform asks for administrator rights through UAC.
    /// </summary>
    public sealed partial class ReadinessDialog : ContentDialog
    {
        private readonly MainViewModel _viewModel;
        private readonly IntPtr _ownerWindow;

        public ReadinessDialog(MainViewModel viewModel, IntPtr ownerWindow)
        {
            InitializeComponent();
            DialogWidth.Fit(this, 600);
            _viewModel = viewModel;
            _ownerWindow = ownerWindow;
            DataContext = viewModel;

            WingetBox.Text = MainViewModel.QemuWingetCommand;
            QemuSiteLink.NavigateUri = new Uri(MainViewModel.QemuDownloadUrl);

            viewModel.RefreshReadiness();
            ShowState();
            viewModel.PropertyChanged += OnViewModelChanged;
            Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
        }

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.IsQemuReady) or nameof(MainViewModel.IsAccelerationReady) or null or "")
                ShowState();
        }

        /// <summary>A tick or a warning per part; the way to install QEMU only while it is missing.</summary>
        private void ShowState()
        {
            SetIcon(QemuIcon, _viewModel.IsQemuReady);
            SetIcon(AccelerationIcon, _viewModel.IsAccelerationReady);
            QemuInstallPanel.Visibility = _viewModel.IsQemuReady ? Visibility.Collapsed : Visibility.Visible;
            // A ready PC is never shown the assistant at start: nothing to opt out of.
            DismissBox.Visibility = _viewModel.IsPcReady ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetIcon(FontIcon icon, bool ready)
        {
            icon.Glyph = ready ? "" : "";
            // The dialog's own theme (ThemeBrushes): a popup is out of the window's tree.
            if (Controls.ThemeBrushes.Get(this, ready ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush") is { } brush)
                icon.Foreground = brush;
        }

        private void OnCopyWingetClick(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(MainViewModel.QemuWingetCommand);
            try
            {
                Clipboard.SetContent(package);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Held by another program: the command stays selectable in its box.
                WingetBox.SelectAll();
            }
        }

        private async void OnChooseQemuFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);
            var folder = await picker.PickSingleFolderAsync();
            if (folder?.Path is { Length: > 0 } path)
                await RunAsync(() => _viewModel.SetQemuDirectoryAsync(path));
        }

        private async void OnRedetectClick(object sender, RoutedEventArgs e) =>
            await RunAsync(() => _viewModel.InitializeAsync());

        private async void OnEnableAccelerationClick(object sender, RoutedEventArgs e)
        {
            EnableButton.IsEnabled = false;
            EnableResult.Text = await _viewModel.EnableAccelerationAsync();
            EnableResult.Visibility = Visibility.Visible;
            EnableButton.IsEnabled = true;
        }

        /// <summary>Detection runs QEMU and reads the disk: a couple of seconds, with the ring turning.</summary>
        private async Task RunAsync(Func<Task> action)
        {
            BusyRing.IsActive = true;
            try
            {
                await action();
                _viewModel.RefreshReadiness();
            }
            finally
            {
                BusyRing.IsActive = false;
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape)
                return;
            e.Handled = true;
            Hide();
        }
    }
}
