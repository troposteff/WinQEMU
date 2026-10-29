using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// Asks for the clone's name and kind, then runs the clone itself so the progress and a way to cancel
    /// stay on screen: a full copy of tens of gigabytes takes minutes, and a dialog that closed straight away
    /// would leave nothing to watch or stop.
    /// </summary>
    public sealed partial class CloneVmDialog : ContentDialog
    {
        private readonly Func<string, bool, IProgress<double>, CancellationToken, Task<VmConfig>> _clone;
        private readonly string _fullCopySize;
        private CancellationTokenSource? _cancellation;

        /// <param name="clone">
        /// Performs the clone under the given name, linked when the flag is set. Called on the UI thread.
        /// </param>
        public CloneVmDialog(
            VmConfig source,
            string suggestedName,
            Func<string, bool, IProgress<double>, CancellationToken, Task<VmConfig>> clone)
        {
            InitializeComponent();
            _clone = clone;

            NameBox.Text = suggestedName;
            NameBox.SelectAll();

            // An upper bound: the copy only takes what the guest has actually written, but the space the
            // disks may grow to is what the host has to be ready for.
            var capacity = source.Disks.Sum(d => d.CapacityBytes);
            _fullCopySize = capacity > 0
                ? string.Format(CultureInfo.CurrentCulture, DialogText.CloneFullCopySize, source.Disks.Count, Sizes.Format(capacity))
                : string.Format(CultureInfo.CurrentCulture, DialogText.CloneFullCopySizeUnknown, source.Disks.Count);

            // Offered but disabled with the reason, rather than hidden: the user then knows what would
            // make it possible.
            if (VmLibrary.WhyNoLinkedClone(source) is { } reason)
            {
                LinkedOption.IsEnabled = false;
                LinkedUnavailableText.Text = reason;
                LinkedUnavailableText.Visibility = Visibility.Visible;
            }
            else if (source.IsTemplate)
            {
                // What a template is for: machines in seconds that share its disks.
                LinkedOption.IsChecked = true;
            }
            // Set here rather than bound in XAML: x:Bind runs at Loading and would put the clone's wording back.
            TitleText.Text = string.Format(CultureInfo.CurrentCulture, source.IsTemplate ? DialogText.DeployTitle : DialogText.CloneTitle, source.Name);
            LinkedHintText.Text = source.IsTemplate ? DialogText.DeployLinkedHint : DialogText.CloneLinkedHint;
            CloneButton.Content = source.IsTemplate ? DialogText.DeployButton : DialogText.CloneButton;

            UpdateSizeText();
        }

        /// <summary>The new machine, or null when the dialog was closed without cloning.</summary>
        public VmConfig? Clone { get; private set; }

        private bool IsCloning => _cancellation is not null;

        private bool IsLinked => LinkedOption.IsChecked == true;

        private void OnKindChanged(object sender, RoutedEventArgs e) => UpdateSizeText();

        private void UpdateSizeText()
        {
            // Raised from InitializeComponent by the initially checked option, before the field is set.
            if (SizeText is null || _fullCopySize is null)
                return;

            SizeText.Text = IsLinked
                ? DialogText.CloneLinkedSize
                : _fullCopySize;
        }

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
            var linked = IsLinked;
            SetCloning(true, linked);

            // Created here, on the UI thread, so each report is posted back to it.
            var progress = new Progress<double>(fraction =>
            {
                CopyProgress.Value = fraction;
                ProgressText.Text = string.Format(CultureInfo.CurrentCulture, DialogText.CloneProgress, fraction);
            });

            try
            {
                Clone = await _clone(NameBox.Text.Trim(), linked, progress, cancellation.Token);
                _cancellation = null;
                Hide();
                return;
            }
            catch (OperationCanceledException)
            {
                // The half-made clone has already been removed; the dialog closes as if nothing happened.
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
            SetCloning(false, linked);
        }

        private void SetCloning(bool cloning, bool linked)
        {
            NameBox.IsEnabled = !cloning;
            FullCopyOption.IsEnabled = !cloning;
            LinkedOption.IsEnabled = !cloning && LinkedUnavailableText.Visibility != Visibility.Visible;
            CloneButton.IsEnabled = !cloning;
            CancelButton.IsEnabled = true;
            ProgressPanel.Visibility = cloning ? Visibility.Visible : Visibility.Collapsed;
            // A linked clone only creates empty overlays: there is no fraction worth showing.
            CopyProgress.IsIndeterminate = cloning && linked;
            CopyProgress.Value = 0;
            ProgressText.Text = linked ? DialogText.CloneCreatingLinked : DialogText.Preparing;
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e) => ShowError(null);

        private void OnCancelClick(object sender, RoutedEventArgs e) => CancelOrClose();

        /// <summary>During a clone, Annuler and Échap stop it; the dialog closes once it has unwound.</summary>
        private void CancelOrClose()
        {
            if (IsCloning)
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
            // Never close over a running clone: nobody would await it any more, so a failure would go
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
