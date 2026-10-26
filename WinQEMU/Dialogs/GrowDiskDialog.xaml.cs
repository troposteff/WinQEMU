using System;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;

namespace WinQEMU.Dialogs
{
    /// <summary>Asks for the new capacity of a disk, floored at one gibibyte above the current one.</summary>
    public sealed partial class GrowDiskDialog : ContentDialog
    {
        private const double BytesPerGib = 1024d * 1024 * 1024;

        private readonly double _currentGib;

        public GrowDiskDialog(DiskConfig disk)
        {
            InitializeComponent();

            _currentGib = disk.CapacityBytes / BytesPerGib;

            TitleText.Text = $"Agrandir « {disk.Id} »";
            CurrentText.Text = disk.CapacityBytes > 0
                ? $"Taille actuelle : {_currentGib:0.##} Gio."
                : "Taille actuelle inconnue : l'image n'a pas été mesurée.";

            // qemu-img refuses anything not larger, so the box cannot offer it in the first place.
            var minimum = Math.Max(1, Math.Ceiling(_currentGib) + 1);
            CapacityBox.Minimum = minimum;
            CapacityBox.Value = minimum;
        }

        public bool Confirmed { get; private set; }

        public long CapacityBytes => (long)Math.Round(CapacityBox.Value * BytesPerGib);

        private void OnConfirmClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // NumberBox leaves Value as NaN when emptied, which would ask for a zero-byte disk.
            if (double.IsNaN(CapacityBox.Value) || CapacityBox.Value <= _currentGib)
                return;

            Confirmed = true;
            Hide();
        }

        private void OnCancelClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Hide();

        private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Escape:
                    e.Handled = true;
                    Hide();
                    break;

                case Windows.System.VirtualKey.Enter:
                    e.Handled = true;
                    OnConfirmClick(sender, new Microsoft.UI.Xaml.RoutedEventArgs());
                    break;
            }
        }
    }
}
