using System;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;

namespace WinQEMU.Dialogs
{
    /// <summary>Collects the three things qemu-img needs to create a disk: size, format and controller.</summary>
    public sealed partial class NewDiskDialog : ContentDialog
    {
        public NewDiskDialog(DiskBus suggestedBus)
        {
            InitializeComponent();

            FormatBox.ItemsSource = Enum.GetValues<DiskFormat>();
            FormatBox.SelectedItem = DiskFormat.Qcow2;

            BusBox.ItemsSource = Enum.GetValues<DiskBus>();
            // Defaults to the bus the machine's other disks already use, so a second disk does not
            // arrive on a controller the guest has no driver for.
            BusBox.SelectedItem = suggestedBus;
        }

        /// <summary>True when the user confirmed. The dialog uses its own buttons, so the result cannot carry this.</summary>
        public bool Confirmed { get; private set; }

        public long CapacityBytes => (long)Math.Round(CapacityBox.Value) * 1024 * 1024 * 1024;

        public DiskFormat Format => FormatBox.SelectedItem is DiskFormat format ? format : DiskFormat.Qcow2;

        public DiskBus Bus => BusBox.SelectedItem is DiskBus bus ? bus : DiskBus.VirtioBlk;

        private void OnCreateClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // NumberBox leaves Value as NaN when the box is emptied, which would create a 0-byte image.
            if (double.IsNaN(CapacityBox.Value) || CapacityBox.Value < 1)
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
                    OnCreateClick(sender, new Microsoft.UI.Xaml.RoutedEventArgs());
                    break;
            }
        }
    }
}
