using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Text;

namespace WinQEMU.Dialogs
{
    /// <summary>Chooses the format to convert a disk into.</summary>
    public sealed partial class ConvertDiskDialog : ContentDialog
    {
        public ConvertDiskDialog(DiskConfig disk)
        {
            InitializeComponent();

            TitleText.Text = string.Format(CultureInfo.CurrentCulture, DialogText.ConvertTitle, disk.Id);
            CurrentText.Text = string.Format(CultureInfo.CurrentCulture, DialogText.ConvertCurrentFormat, disk.Format.ToString().ToLowerInvariant());

            // Converting to the format it already has would only duplicate the file.
            var options = Enum.GetValues<DiskFormat>().Where(f => f != disk.Format).ToList();
            FormatBox.ItemsSource = options;
            // qcow2 when it is on offer, since it is the only format that gains snapshots and sparseness.
            FormatBox.SelectedItem = options.Contains(DiskFormat.Qcow2) ? DiskFormat.Qcow2 : options[0];
        }

        public bool Confirmed { get; private set; }

        public DiskFormat Format => FormatBox.SelectedItem is DiskFormat format ? format : DiskFormat.Qcow2;

        /// <summary>Compression is a qcow2 feature; the flag is meaningless for the other formats.</summary>
        public bool Compress => Format == DiskFormat.Qcow2 && CompressBox.IsChecked == true;

        private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            var isQcow2 = Format == DiskFormat.Qcow2;
            CompressBox.IsEnabled = isQcow2;
            if (!isQcow2)
                CompressBox.IsChecked = false;
        }

        private void OnConfirmClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (FormatBox.SelectedItem is null)
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
