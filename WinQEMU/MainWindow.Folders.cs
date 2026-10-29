using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinQEMU.ViewModels;

namespace WinQEMU
{
    /// <summary>A folder's machines as one (ViewModels.MainViewModel.Folders): its summary's buttons and its menu in the list.</summary>
    public sealed partial class MainWindow
    {
        private void OnStartFolderClick(object sender, RoutedEventArgs e) => RunOnSelectedFolder(id => _viewModel.StartFolderAsync(id));

        private void OnStopFolderClick(object sender, RoutedEventArgs e) => RunOnSelectedFolder(_viewModel.StopFolderAsync);

        private void OnPauseFolderClick(object sender, RoutedEventArgs e) => RunOnSelectedFolder(_viewModel.PauseFolderAsync);

        private void OnResumeFolderClick(object sender, RoutedEventArgs e) => RunOnSelectedFolder(_viewModel.ResumeFolderAsync);

        private void OnCancelFolderStartClick(object sender, RoutedEventArgs e) => _viewModel.CancelFolderStart();

        private void RunOnSelectedFolder(Func<string, Task<MainViewModel.FolderOutcome>> action)
        {
            if (_viewModel.SelectedFolder is { } folder)
                _ = RunOnFolderAsync(folder.Id, action);
        }

        /// <summary>A failed machine is said in the error bar as it fails; nothing more is shown when the folder is done.</summary>
        private async Task RunOnFolderAsync(string folderId, Func<string, Task<MainViewModel.FolderOutcome>> action)
        {
            try
            {
                await action(folderId);
            }
            catch (InvalidOperationException ex)
            {
                ShowLibraryError(ex.Message);
            }
        }
    }
}
