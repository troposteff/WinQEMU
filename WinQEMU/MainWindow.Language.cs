using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>
    /// The language changed in the settings (ViewModels.MainViewModel.SelectedLanguage): the window takes it at once, as
    /// it does a theme, rather than at the next start. The view-models have already raised their own text again.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _applyingLanguage;

        private void ApplyLanguage()
        {
            _applyingLanguage = true;
            try
            {
                // Every {x:Bind t:…} of the window is one-time: Update reads them all again. It also puts back the
                // tooltips' plain strings, which NameInTooltips then dresses again.
                Bindings.Update();
                NameInTooltips(MachineToolbar);
                if (FullScreenBar.Child is Panel fullScreenCommands)
                    NameInTooltips(fullScreenCommands);

                // Same index, new words: the mode itself does not change.
                var scale = ScaleModeBox.SelectedIndex;
                ScaleModeBox.ItemsSource = new[] { ShellText.ScaleFit, ShellText.ScaleActual };
                ScaleModeBox.SelectedIndex = scale;
                UpdateGuestSizeText();
                ConsoleHost.RefreshText();

                RefreshItemTemplates(RootGrid);
            }
            finally
            {
                _applyingLanguage = false;
            }

            // Drawn from code with their words in them, as for a theme.
            DispatcherQueue.TryEnqueue(() =>
            {
                DrawSnapshotTree();
                DrawMetrics();
            });
        }

        /// <summary>
        /// The text inside item templates (a disk's buttons, a card's labels, the enum names of a list) is bound per item,
        /// out of reach of Bindings.Update: taking the template off and putting it back has every item drawn anew. The
        /// library list is left alone — its lines are bound to rows that raised their own text, and rebuilding it would
        /// drop its selection, and with it the console.
        /// </summary>
        private static void RefreshItemTemplates(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                // A section is named after its header once, when the control is templated: Narrator kept reading
                // « Disques » under an English « Disks », even with the header set again. Named here instead.
                if (child is Expander { Header: TextBlock header } expander)
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, header.Text);
                if (child is ItemsControl items and not Controls.LibraryListView)
                {
                    if (items.ItemTemplate is { } template)
                    {
                        items.ItemTemplate = null;
                        items.ItemTemplate = template;
                    }
                    else if (!string.IsNullOrEmpty(items.DisplayMemberPath))
                    {
                        var path = items.DisplayMemberPath;
                        items.DisplayMemberPath = string.Empty;
                        items.DisplayMemberPath = path;
                    }
                }
                RefreshItemTemplates(child);
            }
        }
    }
}
