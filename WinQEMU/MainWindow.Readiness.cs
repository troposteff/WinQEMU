using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace WinQEMU
{
    /// <summary>The readiness assistant (Dialogs.ReadinessDialog): at start while this PC is not ready, from the settings, from the missing-QEMU bar.</summary>
    public sealed partial class MainWindow
    {
        /// <summary>Once the library is read: nothing is said to a PC that is ready, or whose user asked not to be told.</summary>
        private async Task OfferReadinessAsync()
        {
            if (_viewModel.ShouldOfferReadiness)
                await ShowReadinessAsync();
        }

        private async Task ShowReadinessAsync()
        {
            var dialog = new Dialogs.ReadinessDialog(_viewModel, WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
                XamlRoot = Content.XamlRoot
            };
            await ShowDialogAsync(dialog);
        }

        private async void OnReadinessClick(object sender, RoutedEventArgs e) => await ShowReadinessAsync();
    }
}
