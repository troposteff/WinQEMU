using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// The CPU's advanced settings: a box per CPUID flag this machine's QEMU recognizes (its <c>-cpu help</c>), ticked
        /// for those the machine turns on. The model and the other options (-flag, key=value) are kept as they are.
        /// </summary>
        private async void OnCpuAdvancedClick(object sender, RoutedEventArgs e)
        {
            var hardware = _viewModel.Hardware;
            var enabled = new HashSet<string>(hardware.EnabledCpuFlags, StringComparer.Ordinal);
            // A flag the machine has but this QEMU does not list stays offered, so it is not dropped by saving.
            var names = hardware.CpuFlagOptions.Union(enabled).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var boxes = names.Select(n => new CheckBox { Content = n, IsChecked = enabled.Contains(n), MinWidth = 0 }).ToList();

            var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 168 };
            foreach (var box in boxes)
                grid.Children.Add(box);

            var search = new TextBox { PlaceholderText = DialogText.CpuFlagsSearch };
            AutomationPropertiesName(search, DialogText.CpuFlagsSearch);
            search.TextChanged += (_, _) =>
            {
                var text = search.Text.Trim();
                foreach (var box in boxes)
                    box.Visibility = ((string)box.Content).Contains(text, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible : Visibility.Collapsed;
            };

            var dialog = new ContentDialog
            {
                Title = DialogText.CpuFlagsTitle,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = DialogText.CpuFlagsHint, TextWrapping = TextWrapping.Wrap },
                        search,
                        new ScrollViewer { Content = grid, MaxHeight = 400 }
                    }
                },
                PrimaryButtonText = CommonText.Ok,
                SecondaryButtonText = DialogText.CpuFlagsDefaults,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            switch (await ShowDialogAsync(dialog))
            {
                case ContentDialogResult.Primary:
                    hardware.SetCpuFlags(boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content));
                    break;
                case ContentDialogResult.Secondary:
                    hardware.SetCpuFlags(CpuModelSpec.EnabledFlags(VmConfig.DefaultCpuModel));
                    break;
            }
        }

        private static void AutomationPropertiesName(DependencyObject element, string name) =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
    }
}
