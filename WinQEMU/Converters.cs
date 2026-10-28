using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;
using WinQEMU.Text;
using WinQEMU.ViewModels;

namespace WinQEMU
{
    /// <summary>Shows an element only when the bound flag is true.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            value is Visibility.Visible;
    }

    /// <summary>
    /// Shows an element only while the bound enum equals the value named in ConverterParameter. Used to
    /// reveal the fields that belong to one network mode without showing them for the others, where they
    /// would suggest settings that are simply ignored.
    /// </summary>
    public sealed class EnumMatchVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>Formats a provisioned disk size for display; 0 means the image has not been measured.</summary>
    public sealed class DiskCapacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var bytes = value switch
            {
                long l => l,
                int i => i,
                _ => 0L
            };

            return bytes <= 0
                ? DialogText.SizeUnknown
                : Sizes.Format(bytes);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Renders the domain enums as the words a user would recognise, rather than the identifier names.
    /// The descriptions live on the view model so the wizard and the summary cannot drift apart.
    /// </summary>
    public sealed class EnumLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => value switch
        {
            FirmwareType firmware => NewVmViewModel.DescribeFirmware(firmware),
            DiskBus bus => NewVmViewModel.DescribeBus(bus),
            NetworkMode mode => NewVmViewModel.DescribeNetwork(mode),
            DiskFormat format => format.ToString().ToLowerInvariant(),
            AcceleratorType accelerator => accelerator switch
            {
                AcceleratorType.Auto => DialogText.AcceleratorAuto,
                AcceleratorType.Whpx => DialogText.AcceleratorWhpx,
                AcceleratorType.Tcg => DialogText.AcceleratorTcg,
                _ => accelerator.ToString()
            },
            GraphicsAdapter graphics => graphics switch
            {
                GraphicsAdapter.VirtioVga => DialogText.GraphicsVirtio,
                GraphicsAdapter.Std => DialogText.GraphicsStd,
                GraphicsAdapter.Qxl => "qxl",
                GraphicsAdapter.Vmware => "vmware",
                GraphicsAdapter.None => DialogText.GraphicsNone,
                _ => graphics.ToString()
            },
            GuestArchitecture architecture => architecture switch
            {
                GuestArchitecture.X86_64 => "x86-64",
                GuestArchitecture.Aarch64 => "ARM64",
                _ => architecture.ToString()
            },
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        };

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
