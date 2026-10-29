using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>Templates (ViewModels.MainViewModel.Templates): making one, deploying it, turning it back into a machine.</summary>
    public sealed partial class MainWindow
    {
        private async void OnMakeTemplateClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedVm is not { } machine || !_viewModel.CanToggleTemplate)
                return;

            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = string.Format(CultureInfo.CurrentCulture, ShellText.TemplateMakeTitle, machine.Name),
                Content = DialogBody(ShellText.TemplateMakeBody),
                PrimaryButtonText = ShellText.TemplateMakeButton,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            });
            if (choice != ContentDialogResult.Primary)
                return;

            SetTemplate(true);
        }

        private void OnConvertToMachineClick(object sender, RoutedEventArgs e) => SetTemplate(false);

        /// <summary>The clone dialog, which offers a template's linked clone first (Dialogs.CloneVmDialog).</summary>
        private void OnDeployClick(object sender, RoutedEventArgs e) => OnCloneClick(sender, e);

        private void SetTemplate(bool isTemplate) => _viewModel.SetSelectedTemplate(isTemplate);
    }
}
