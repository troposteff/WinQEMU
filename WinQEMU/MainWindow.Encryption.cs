using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;

namespace WinQEMU
{
    /// <summary>
    /// Encrypted disks (Core.Qemu.DiskKey): the password prompt, and the actions of the summary's « Chiffrement des
    /// disques » section. One ContentDialog at a time in WinUI: a password is always asked before a progress dialog
    /// opens, never from inside one.
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Asks for a machine's password, with « Mémoriser » unchecked: nothing is kept unless the user says so.
        /// Null when cancelled.
        /// </summary>
        private async Task<DiskPasswordAnswer?> PromptDiskPasswordAsync(VmConfig machine, string? problem)
        {
            var box = new PasswordBox { Header = EncryptionText.PasswordLabel };
            var remember = new CheckBox { Content = EncryptionText.Remember, IsChecked = false };
            var body = new StackPanel { Spacing = 10, MinWidth = 420 };
            body.Children.Add(DialogBody(string.Format(CultureInfo.CurrentCulture, EncryptionText.PromptBody, machine.Name), 420));
            if (problem is not null)
                body.Children.Add(ErrorText(problem));
            body.Children.Add(box);
            body.Children.Add(remember);
            body.Children.Add(Hint(EncryptionText.RememberHint));

            var dialog = new ContentDialog
            {
                Title = EncryptionText.PromptTitle,
                Content = body,
                PrimaryButtonText = EncryptionText.Unlock,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Content.XamlRoot
            };
            box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
            box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

            return await ShowDialogAsync(dialog) == ContentDialogResult.Primary
                ? new DiskPasswordAnswer(box.Password, remember.IsChecked == true)
                : null;
        }

        /// <summary>
        /// Unlocks the selection when its disks are encrypted (from memory, the Credential Manager or the prompt). False
        /// when the user cancelled or it failed, the failure being said.
        /// </summary>
        private async Task<bool> UnlockSelectedAsync()
        {
            if (_viewModel.SelectedVm is not { DisksEncrypted: true } machine)
                return true;
            try
            {
                await _viewModel.Manager.UnlockAsync(machine);
                _viewModel.RefreshEncryption();
                return true;
            }
            catch (DiskUnlockCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is QemuImgException or DiskPasswordException or InvalidOperationException or System.IO.IOException)
            {
                await ShowProblemAsync(EncryptionText.UnlockFailedTitle, ex.Message);
                return false;
            }
        }

        private static TextBlock ErrorText(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };

        private static TextBlock Hint(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["Hint"]
        };

        /// <summary>A new password typed twice, with « Mémoriser »; null when cancelled.</summary>
        private async Task<DiskPasswordAnswer?> AskNewPasswordAsync(string title, string text, string button, bool rememberByDefault)
        {
            var first = new PasswordBox { Header = EncryptionText.NewPasswordLabel };
            var second = new PasswordBox { Header = EncryptionText.ConfirmPasswordLabel };
            var remember = new CheckBox { Content = EncryptionText.Remember, IsChecked = rememberByDefault };
            var error = ErrorText(string.Empty);
            error.Visibility = Visibility.Collapsed;

            var body = new StackPanel { Spacing = 10, MinWidth = 460 };
            body.Children.Add(DialogBody(text));
            body.Children.Add(first);
            body.Children.Add(second);
            body.Children.Add(error);
            body.Children.Add(remember);
            body.Children.Add(Hint(EncryptionText.RememberHint));

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = button,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Content.XamlRoot
            };

            void Validate()
            {
                var problem = second.Password.Length == 0 ? null : DiskKey.WhyPasswordIsRefused(first.Password, second.Password);
                error.Text = problem ?? string.Empty;
                error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = DiskKey.WhyPasswordIsRefused(first.Password, second.Password) is null;
            }
            first.PasswordChanged += (_, _) => Validate();
            second.PasswordChanged += (_, _) => Validate();

            return await ShowDialogAsync(dialog) == ContentDialogResult.Primary
                ? new DiskPasswordAnswer(first.Password, remember.IsChecked == true)
                : null;
        }

        /// <summary>
        /// Runs a long disk operation behind a dialog with a progress bar (or a ring, without progress), and says what
        /// went wrong. True when it succeeded.
        /// </summary>
        private async Task<bool> RunWithProgressAsync(string title, Func<IProgress<double>, Task> operation, bool determinate = true)
        {
            FrameworkElement content = determinate
                ? new ProgressBar { Maximum = 1, MinWidth = 360 }
                : new ProgressRing { IsActive = true, Width = 40, Height = 40 };
            var dialog = new ContentDialog { Title = title, Content = content, XamlRoot = Content.XamlRoot };
            // Shown first, hidden once done: hidden before it opened, it would stay open for good.
            var showing = ShowDialogAsync(dialog);
            Exception? failure = null;
            try
            {
                await operation(new Progress<double>(p => { if (content is ProgressBar bar) bar.Value = p; }));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            dialog.Hide();
            await showing;
            _viewModel.RefreshEncryption();
            await _viewModel.Snapshots.RefreshAsync();

            if (failure is null)
                return true;
            await ShowProblemAsync(EncryptionText.OperationFailedTitle, failure.Message);
            return false;
        }

        private async void OnEncryptDisksClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is not { } machine || _viewModel.Manager.DiskService is not { } images)
                return;
            if (_viewModel.Manager.WhyCannotEncrypt(machine) is { } refusal)
            {
                await ShowProblemAsync(EncryptionText.EncryptNotPossibleTitle, refusal);
                return;
            }
            if (await DiskEncryption.WhyNotEnoughSpaceAsync(machine, images) is { } space)
            {
                await ShowProblemAsync(EncryptionText.EncryptNotPossibleTitle, space);
                return;
            }

            var answer = await AskNewPasswordAsync(
                string.Format(CultureInfo.CurrentCulture, EncryptionText.EncryptTitle, machine.Name),
                EncryptionText.EncryptBody, EncryptionText.EncryptButton, rememberByDefault: false);
            if (answer is null)
                return;

            DiskEncryptionResult? result = null;
            if (!await RunWithProgressAsync(EncryptionText.EncryptProgress, async progress =>
                    result = await _viewModel.Manager.EncryptVirtualMachineAsync(machine, new DiskKey(answer.Password), answer.Remember, progress)))
                return;
            _viewModel.AppendDiagnostic($"{machine.Name}: disks encrypted.");
            await ShowLeftInPlaceAsync(result);
        }

        private async void OnDecryptDisksClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is not { } machine || _viewModel.Manager.DiskService is not { } images)
                return;
            if ((_viewModel.Manager.WhyCannotDecrypt(machine) ?? await DiskEncryption.WhyNotEnoughSpaceAsync(machine, images)) is { } refusal)
            {
                await ShowProblemAsync(EncryptionText.DecryptNotPossibleTitle, refusal);
                return;
            }

            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, EncryptionText.DecryptTitle, machine.Name),
                Content = DialogBody(EncryptionText.DecryptBody),
                PrimaryButtonText = EncryptionText.DecryptButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            });
            if (choice != ContentDialogResult.Primary || !await UnlockSelectedAsync())
                return;

            DiskEncryptionResult? result = null;
            if (!await RunWithProgressAsync(EncryptionText.DecryptProgress, async progress =>
                    result = await _viewModel.Manager.DecryptVirtualMachineAsync(machine, progress)))
                return;
            _viewModel.AppendDiagnostic($"{machine.Name}: disks decrypted.");
            await ShowLeftInPlaceAsync(result);
        }

        private async void OnChangeDiskPasswordClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is not { } machine)
                return;
            if (_viewModel.Manager.WhyCannotChangeDiskPassword(machine) is { } refusal)
            {
                await ShowProblemAsync(EncryptionText.ChangeNotPossibleTitle, refusal);
                return;
            }
            // The current password first: amend opens every image with it.
            if (!await UnlockSelectedAsync())
                return;

            var answer = await AskNewPasswordAsync(
                string.Format(CultureInfo.CurrentCulture, EncryptionText.ChangeTitle, machine.Name),
                EncryptionText.ChangeBody, EncryptionText.ChangeButton, _viewModel.Manager.IsDiskPasswordRemembered(machine));
            if (answer is null)
                return;

            if (await RunWithProgressAsync(EncryptionText.ChangeProgress,
                    _ => _viewModel.Manager.ChangeDiskPasswordAsync(machine, new DiskKey(answer.Password), answer.Remember), determinate: false))
                _viewModel.AppendDiagnostic($"{machine.Name}: disk password changed.");
        }

        private async void OnForgetDiskPasswordClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is not { } machine)
                return;
            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = EncryptionText.ForgetTitle,
                Content = DialogBody(string.Format(CultureInfo.CurrentCulture, EncryptionText.ForgetBody, machine.Name)),
                PrimaryButtonText = EncryptionText.ForgetButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (choice == ContentDialogResult.Primary)
                _viewModel.ForgetSelectedPassword();
        }

        /// <summary>Images attached from outside the machine's folder are left where they are: said, with their paths.</summary>
        private async Task ShowLeftInPlaceAsync(DiskEncryptionResult? result)
        {
            if (result is null || result.ImagesLeftInPlace.Count == 0)
                return;
            await ShowProblemAsync(EncryptionText.LeftInPlaceTitle,
                string.Format(CultureInfo.CurrentCulture, EncryptionText.LeftInPlaceBody, string.Join(Environment.NewLine, result.ImagesLeftInPlace)));
        }
    }
}
