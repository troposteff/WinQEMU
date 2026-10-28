using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Export;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Asks where to write the .ova, says what will not come across, then runs the export itself so the
    /// progress and a way to cancel stay on screen, as the clone dialog does: compressing tens of gigabytes
    /// takes minutes.
    /// </summary>
    public sealed partial class ExportVmDialog : ContentDialog
    {
        private readonly Func<string, IProgress<double>, CancellationToken, Task> _export;
        private readonly Func<string, Task<string?>> _pickDestination;
        private readonly bool _blocked;
        private CancellationTokenSource? _cancellation;

        // The file the save picker confirmed overwriting; a typed name of an existing file is refused, since
        // nothing would have asked.
        private string? _confirmedOverwrite;

        /// <param name="pickDestination">Shows the save picker from the given path; null when dismissed.</param>
        /// <param name="export">Exports the machine to the given path. Called on the UI thread.</param>
        public ExportVmDialog(
            VmConfig source,
            Func<string, Task<string?>> pickDestination,
            Func<string, IProgress<double>, CancellationToken, Task> export)
        {
            InitializeComponent();
            _export = export;
            _pickDestination = pickDestination;

            TitleText.Text = string.Format(CultureInfo.CurrentCulture, DialogText.ExportTitle, source.Name);
            PathBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OvaExporter.PackageBaseName(source.Name) + ".ova");

            // An upper bound before compression: what the guest has written is all that goes in.
            var capacity = source.Disks.Sum(d => d.CapacityBytes);
            SizeText.Text = string.Format(CultureInfo.CurrentCulture, DialogText.ExportSize, source.Disks.Count, Sizes.Format(capacity));

            foreach (var note in OvaExporter.Notes(source))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = "• " + note,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Style = (Style)Application.Current.Resources["Hint"]
                });
            }

            if (OvaExporter.WhyCannotExport(source) is { } reason)
            {
                _blocked = true;
                ExportButton.IsEnabled = false;
                ShowError(reason);
            }
        }

        /// <summary>The package written, or null when the dialog was closed without exporting.</summary>
        public string? ExportedPath { get; private set; }

        private bool IsExporting => _cancellation is not null;

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            if (await _pickDestination(PathBox.Text.Trim()) is { } picked)
            {
                PathBox.Text = picked;
                _confirmedOverwrite = picked;
            }
        }

        /// <summary>The destination, or null with the reason shown.</summary>
        private string? Destination()
        {
            var text = PathBox.Text.Trim().Trim('"');
            if (text.Length == 0)
            {
                ShowError(DialogText.ExportNoFile);
                return null;
            }

            string path;
            try
            {
                path = Path.GetFullPath(text);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ShowError(DialogText.InvalidPath);
                return null;
            }

            if (!path.EndsWith(".ova", StringComparison.OrdinalIgnoreCase))
                path += ".ova";
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, DialogText.FolderMissing, Path.GetDirectoryName(path)));
                return null;
            }
            if (File.Exists(path) && !string.Equals(path, _confirmedOverwrite, StringComparison.OrdinalIgnoreCase))
            {
                ShowError(DialogText.ExportFileExists);
                return null;
            }

            return path;
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (IsExporting || _blocked || Destination() is not { } destination)
                return;

            ShowError(null);
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            SetExporting(true);

            // Created here, on the UI thread, so each report is posted back to it. The exporter gives the
            // conversion 85 % and writing the package the rest.
            var progress = new Progress<double>(fraction =>
            {
                ExportProgress.Value = fraction;
                ProgressText.Text = fraction < 0.85
                    ? string.Format(CultureInfo.CurrentCulture, DialogText.ExportConverting, fraction)
                    : string.Format(CultureInfo.CurrentCulture, DialogText.ExportWriting, fraction);
            });

            try
            {
                await _export(destination, progress, cancellation.Token);
                ExportedPath = destination;
                _cancellation = null;
                Hide();
                return;
            }
            catch (OperationCanceledException)
            {
                // The partial package and work files are already gone.
                _cancellation = null;
                Hide();
                return;
            }
            catch (Exception ex)
            {
                // Kept open with the reason, so a full disk or a wrong folder can be fixed and retried.
                ShowError(ex is IOException { HResult: unchecked((int)0x80070070) }
                    ? DialogText.ExportDiskFull
                    : ex.Message);
            }

            _cancellation = null;
            SetExporting(false);
        }

        private void SetExporting(bool exporting)
        {
            PathBox.IsEnabled = !exporting;
            BrowseButton.IsEnabled = !exporting;
            ExportButton.IsEnabled = !exporting;
            CancelButton.IsEnabled = true;
            ProgressPanel.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
            ExportProgress.Value = 0;
            ProgressText.Text = DialogText.Preparing;
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnPathChanged(object sender, TextChangedEventArgs e)
        {
            if (!_blocked)
                ShowError(null);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => CancelOrClose();

        /// <summary>During an export, Annuler and Échap stop it; the dialog closes once it has unwound.</summary>
        private void CancelOrClose()
        {
            if (IsExporting)
            {
                ProgressText.Text = DialogText.Cancelling;
                CancelButton.IsEnabled = false;
                _cancellation!.Cancel();
                return;
            }

            Hide();
        }

        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // Never close over a running export: nobody would await it, so a failure would go unreported.
            if (IsExporting)
            {
                args.Cancel = true;
                CancelOrClose();
            }
        }

        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    e.Handled = true;
                    CancelOrClose();
                    break;

                case Windows.System.VirtualKey.Enter:
                    e.Handled = true;
                    OnExportClick(sender, new RoutedEventArgs());
                    break;
            }
        }
    }
}
