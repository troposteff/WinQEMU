using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>"Copy diagnostics" above the machine's log (ViewModels.MainViewModel.Diagnostics).</summary>
    public sealed partial class MainWindow
    {
        private const string IssuesUrl = "https://github.com/troposteff/WinQEMU/issues/new";

        private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement button)
                return;

            bool copied;
            try
            {
                var package = new DataPackage();
                package.SetText(_viewModel.BuildDiagnosticReport());
                Clipboard.SetContent(package);
                Clipboard.Flush();
                copied = true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program holds the clipboard.
                copied = false;
            }

            var open = new HyperlinkButton { Content = ShellText.DiagnosticsOpenIssues, Padding = new Thickness(0) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(IssuesUrl) { UseShellExecute = true });
            var panel = new StackPanel
            {
                Spacing = 6,
                MaxWidth = 360,
                Children =
                {
                    new TextBlock
                    {
                        Text = copied ? ShellText.DiagnosticsCopied : ShellText.DiagnosticsNotCopied,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };
            if (copied)
                panel.Children.Add(open);

            ShowAboveConsole(new Flyout { Content = panel }, button);
        }
    }
}
