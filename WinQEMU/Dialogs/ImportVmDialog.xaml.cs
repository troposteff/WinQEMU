using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Import;
using WinQEMU.Core.Models;
using WinQEMU.Core.Services;
using WinQEMU.ViewModels;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Shows what a foreign machine becomes here, then runs the import itself, as the clone dialog does:
    /// converting tens of gigabytes takes minutes, and the progress and a way to cancel have to stay on
    /// screen. A machine that cannot be imported is still shown, with the reason and no Importer button.
    /// </summary>
    public sealed partial class ImportVmDialog : ContentDialog
    {
        private readonly ImportPlan _plan;
        private readonly Func<ImportPlan, string, IProgress<double>, CancellationToken, Task<VmConfig>> _import;
        private CancellationTokenSource? _cancellation;

        /// <param name="import">Performs the import under the given name. Called on the UI thread.</param>
        public ImportVmDialog(
            ImportPlan plan,
            string suggestedName,
            Func<ImportPlan, string, IProgress<double>, CancellationToken, Task<VmConfig>> import)
        {
            InitializeComponent();
            _plan = plan;
            _import = import;

            TitleText.Text = plan.Source switch
            {
                ImportSource.VMware => "Importer une machine VMware",
                ImportSource.VirtualBox => "Importer une machine VirtualBox",
                _ => "Importer un disque"
            };
            SourceText.Text = plan.SourcePath;
            NameBox.Text = suggestedName;
            NameBox.SelectAll();
            SummaryText.Text = Summarize(plan);

            foreach (var note in plan.Notes)
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = "• " + note,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["Hint"]
                });
            }
            NotesScroller.Visibility = plan.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (plan.Blocker is not null)
            {
                ShowError(plan.Blocker);
                NameBox.IsEnabled = false;
                ImportButton.IsEnabled = false;
                CancelButton.Content = "Fermer";
            }
        }

        /// <summary>The new machine, or null when the dialog was closed without importing.</summary>
        public VmConfig? Imported { get; private set; }

        private bool IsImporting => _cancellation is not null;

        private static string Summarize(ImportPlan plan)
        {
            var text = new StringBuilder();
            var guest = plan.Profile.DisplayName;
            text.AppendLine(plan.SourceGuestOs.Length > 0
                ? $"Système invité : {guest} (« {plan.SourceGuestOs} »)"
                : $"Système invité : {guest}");
            text.AppendLine($"Microprogramme : {NewVmViewModel.DescribeFirmware(plan.Firmware)}");

            var cpu = plan.CoresPerSocket > 1
                ? $"{plan.CpuCount} processeurs ({plan.CoresPerSocket} cœurs par socket)"
                : $"{plan.CpuCount} processeur(s)";
            text.AppendLine($"{cpu}, {plan.MemoryMb} Mio de mémoire");

            for (var index = 0; index < plan.Disks.Count; index++)
            {
                var disk = plan.Disks[index];
                var size = disk.CapacityBytes > 0
                    ? $"{disk.CapacityBytes / (1024d * 1024 * 1024):0.#} Gio, "
                    : string.Empty;
                text.AppendLine(
                    $"Disque {index.ToString(CultureInfo.CurrentCulture)} : {Path.GetFileName(disk.SourcePath)} ({size}{disk.Format}, " +
                    $"{disk.SourceSlot}) → {NewVmViewModel.DescribeBus(disk.Bus)}");
            }

            foreach (var adapter in plan.Adapters)
            {
                var mac = adapter.MacAddress.Length > 0 ? $", adresse {adapter.MacAddress}" : string.Empty;
                text.AppendLine($"Réseau : {adapter.Model}{mac}, {NewVmViewModel.DescribeNetwork(adapter.Mode)}");
            }

            if (plan.IsoPath.Length > 0)
                text.AppendLine($"Lecteur optique : {plan.IsoPath}");

            return text.ToString().TrimEnd();
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            if (IsImporting || _plan.Blocker is not null)
                return;

            if (VmLibrary.ValidateName(NameBox.Text) is { } problem)
            {
                ShowError(problem);
                return;
            }

            ShowError(null);
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            SetImporting(true);

            // Created here, on the UI thread, so each report is posted back to it.
            var progress = new Progress<double>(fraction =>
            {
                CopyProgress.Value = fraction;
                ProgressText.Text = $"Conversion des disques… {fraction:P0}";
            });

            try
            {
                Imported = await _import(_plan, NameBox.Text.Trim(), progress, cancellation.Token);
                _cancellation = null;
                Hide();
                return;
            }
            catch (OperationCanceledException)
            {
                // The half-made machine has already been removed; the dialog closes as if nothing happened.
                _cancellation = null;
                Hide();
                return;
            }
            catch (Exception ex)
            {
                // Kept open with the reason, so a name clash or a full disk can be fixed and retried.
                ShowError(ex.Message);
            }

            _cancellation = null;
            SetImporting(false);
        }

        private void SetImporting(bool importing)
        {
            NameBox.IsEnabled = !importing;
            ImportButton.IsEnabled = !importing;
            CancelButton.IsEnabled = true;
            ProgressPanel.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
            CopyProgress.Value = 0;
            ProgressText.Text = "Préparation…";
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            // The blocker stays: it is about the source, not the name.
            if (_plan.Blocker is null)
                ShowError(null);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => CancelOrClose();

        /// <summary>During an import, Annuler and Échap stop it; the dialog closes once it has unwound.</summary>
        private void CancelOrClose()
        {
            if (IsImporting)
            {
                ProgressText.Text = "Annulation…";
                CancelButton.IsEnabled = false;
                _cancellation!.Cancel();
                return;
            }

            Hide();
        }

        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // Never close over a running import: nobody would await it any more, so a failure would go
            // unreported and a cancelled import's folder removal unconfirmed.
            if (IsImporting)
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
                    OnImportClick(sender, new RoutedEventArgs());
                    break;
            }
        }
    }
}
