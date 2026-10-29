using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>Console capture (ViewModels.MainViewModel.Capture): saved, copied, and said in a flyout by the button.</summary>
    public sealed partial class MainWindow
    {
        private async void OnCaptureClick(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.CanCapture || sender is not FrameworkElement button)
                return;

            string path;
            try
            {
                path = await _viewModel.CaptureSelectedAsync();
            }
            catch (InvalidOperationException ex)
            {
                await ShowProblemAsync(ShellText.CaptureFailedTitle, ex.Message);
                return;
            }

            var copied = await CopyImageAsync(path);

            // A flyout rather than a dialog: the guest stays in view, and nothing waits for an answer.
            var open = new HyperlinkButton { Content = ShellText.CaptureOpenFolder, Padding = new Thickness(0) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            var flyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 6,
                    MaxWidth = 360,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = string.Format(CultureInfo.CurrentCulture, copied ? ShellText.CaptureSavedCopied : ShellText.CaptureSaved, Path.GetFileName(path)),
                            TextWrapping = TextWrapping.Wrap
                        },
                        open
                    }
                }
            };
            ShowAboveConsole(flyout, button);
        }

        /// <summary>The PNG on the clipboard, as an image and as a file, for pasting into a document or a folder alike.</summary>
        private static async System.Threading.Tasks.Task<bool> CopyImageAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                package.SetStorageItems(new IStorageItem[] { file });
                Clipboard.SetContent(package);
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException)
            {
                // Another program holds the clipboard: the file is there all the same, and the flyout says only that.
                return false;
            }
        }
    }
}
