using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// The wide dialogs (wizard, settings, virtual networks) keep their width while the window has room and narrow
    /// with it: in an 800-pixel window the wizard's 700 px ran past the dialog's edge, cutting its fields and its
    /// Modifier… button.
    /// </summary>
    internal static class DialogWidth
    {
        /// <summary>
        /// What ContentDialog leaves around its content in a narrow window (its margins, the 24-pixel padding and
        /// border of these dialogs' own frame): measured at 800 px, the dialog was 735 wide.
        /// </summary>
        private const double Chrome = 120;

        /// <summary>The dialog's frame is its Content, a Border whose child carries the preferred width.</summary>
        public static void Fit(ContentDialog dialog, double preferred)
        {
            if ((dialog.Content as Border)?.Child is not FrameworkElement body)
                return;

            void Apply(XamlRoot root, XamlRootChangedEventArgs? _) =>
                body.Width = Math.Max(360, Math.Min(preferred, root.Size.Width - Chrome));

            dialog.Opened += (_, _) =>
            {
                Apply(dialog.XamlRoot, null);
                dialog.XamlRoot.Changed += Apply;
            };
            dialog.Closed += (_, _) => dialog.XamlRoot.Changed -= Apply;
        }
    }
}
