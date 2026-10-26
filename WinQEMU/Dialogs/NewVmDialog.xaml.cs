using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinQEMU.Core.Services;
using WinQEMU.ViewModels;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Multi-step VM creation wizard. The primary button doubles as "Suivant" until the last step,
    /// where it becomes "Créer"; navigation is handled by cancelling the button's close behaviour.
    /// </summary>
    public sealed partial class NewVmDialog : ContentDialog
    {
        private readonly IntPtr _ownerWindow;

        public NewVmDialog(VmManager manager, IntPtr ownerWindow)
        {
            InitializeComponent();

            _ownerWindow = ownerWindow;
            ViewModel = new NewVmViewModel(manager);
            DataContext = ViewModel;
        }

        public NewVmViewModel ViewModel { get; }

        /// <summary>
        /// True when the user reached the last step and confirmed. The dialog uses its own buttons, so
        /// <see cref="ContentDialogResult"/> is always None and cannot carry this.
        /// </summary>
        public bool Confirmed { get; private set; }

        /// <summary>The request the user assembled. Only meaningful when <see cref="Confirmed"/> is true.</summary>
        public CreateVmRequest BuildRequest() => ViewModel.BuildRequest();

        private void OnNextClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (!ViewModel.IsLastStep)
            {
                ViewModel.GoNext();
                return;
            }

            Confirmed = true;
            Hide();
        }

        private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ViewModel.GoBack();

        private void OnCancelClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Hide();

        /// <summary>
        /// Restores the keyboard behaviour the built-in buttons provided: Escape cancels, Enter advances.
        /// A multi-line TextBox consumes Enter itself, so Description still accepts line breaks.
        /// </summary>
        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    e.Handled = true;
                    Hide();
                    break;

                case Windows.System.VirtualKey.Enter when ViewModel.IsCurrentStepValid:
                    e.Handled = true;
                    OnNextClick(sender, new Microsoft.UI.Xaml.RoutedEventArgs());
                    break;
            }
        }

        private async void OnBrowseLibraryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is not null)
                ViewModel.ChangeLibraryRoot(folder);
        }

        private async void OnBrowseDiskClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var file = await PickFileAsync(".qcow2", ".img", ".raw", ".vhdx", ".vmdk", ".vdi");
            if (file is not null)
                ViewModel.ExistingDiskPath = file;
        }

        private async void OnBrowseIsoClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var file = await PickFileAsync(".iso", ".img");
            if (file is not null)
                ViewModel.IsoPath = file;
        }

        private async void OnBrowseDriversClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var file = await PickFileAsync(".iso");
            if (file is not null)
                ViewModel.SecondaryIsoPath = file;
        }

        private async Task<string?> PickFileAsync(params string[] extensions)
        {
            var picker = new FileOpenPicker();
            foreach (var extension in extensions)
                picker.FileTypeFilter.Add(extension);

            // Pickers need an owner window; without it they throw in a packaged desktop app.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        private async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
