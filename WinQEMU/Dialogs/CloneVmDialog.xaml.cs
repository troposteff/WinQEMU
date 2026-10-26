using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Asks for the clone's name, then runs the copy itself so the progress and a way to cancel stay on
    /// screen: copying tens of gigabytes takes minutes, and a dialog that closed straight away would leave
    /// nothing to watch or stop.
    /// </summary>
    public sealed partial class CloneVmDialog : ContentDialog
    {
        private readonly Func<string, IProgress<double>, CancellationToken, Task<VmConfig>> _clone;
        private CancellationTokenSource? _cancellation;

        /// <param name="clone">Performs the copy under the given name. Called on the UI thread.</param>
        public CloneVmDialog(
            VmConfig source,
            string suggestedName,
            Func<string, IProgress<double>, CancellationToken, Task<VmConfig>> clone)
        {
            InitializeComponent();
            _clone = clone;

            TitleText.Text = $"Cloner « {source.Name} »";
            NameBox.Text = suggestedName;
            NameBox.SelectAll();

            // An upper bound: the copy only takes what the guest has actually written, but the space the
            // disks may grow to is what the host has to be ready for.
            var capacity = source.Disks.Sum(d => d.CapacityBytes);
            SizeText.Text = capacity > 0
                ? $"{source.Disks.Count} disque(s), {capacity / (1024d * 1024 * 1024):0.#} Gio au plus, copiés dans un nouveau dossier de la bibliothèque."
                : $"{source.Disks.Count} disque(s), copiés dans un nouveau dossier de la bibliothèque.";
        }

        /// <summary>The new machine, or null when the dialog was closed without cloning.</summary>
        public VmConfig? Clone { get; private set; }

        private bool IsCloning => _cancellation is not null;

        private async void OnCloneClick(object sender, RoutedEventArgs e)
        {
            if (IsCloning)
                return;

            if (VmLibrary.ValidateName(NameBox.Text) is { } problem)
            {
                ShowError(problem);
                return;
            }

            ShowError(null);
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            SetCloning(true);

            // Created here, on the UI thread, so each report is posted back to it.
            var progress = new Progress<double>(fraction =>
            {
                CopyProgress.Value = fraction;
                ProgressText.Text = $"Copie des disques… {fraction:P0}";
            });

            try
            {
                Clone = await _clone(NameBox.Text.Trim(), progress, cancellation.Token);
                _cancellation = null;
                Hide();
                return;
            }
            catch (OperationCanceledException)
            {
                // The half-made copy has already been removed; the dialog closes as if nothing happened.
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
            SetCloning(false);
        }

        private void SetCloning(bool cloning)
        {
            NameBox.IsEnabled = !cloning;
            CloneButton.IsEnabled = !cloning;
            CancelButton.IsEnabled = true;
            ProgressPanel.Visibility = cloning ? Visibility.Visible : Visibility.Collapsed;
            CopyProgress.Value = 0;
            ProgressText.Text = "Préparation…";
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e) => ShowError(null);

        private void OnCancelClick(object sender, RoutedEventArgs e) => CancelOrClose();

        /// <summary>During a copy, Annuler and Échap stop it; the dialog closes once it has unwound.</summary>
        private void CancelOrClose()
        {
            if (IsCloning)
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
            // Never close over a running copy: nobody would await it any more, so a failure would go
            // unreported and a cancelled copy's folder removal unconfirmed.
            if (IsCloning)
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
                    OnCloneClick(sender, new RoutedEventArgs());
                    break;
            }
        }
    }
}
