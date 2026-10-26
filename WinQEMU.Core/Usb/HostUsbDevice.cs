using System.Globalization;
using System.Text.RegularExpressions;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Usb
{
    /// <summary>A USB device plugged into the host, as Windows describes it.</summary>
    public sealed partial class HostUsbDevice
    {
        public required string InstanceId { get; init; }
        public required int VendorId { get; init; }
        public required int ProductId { get; init; }
        public required string Name { get; init; }

        /// <summary>The function driver Windows loaded, e.g. "USBSTOR", "WinUSB", "BTHUSB".</summary>
        public string Service { get; init; } = string.Empty;

        public IReadOnlyList<string> LowerFilters { get; init; } = Array.Empty<string>();

        public string IdText => UsbDeviceConfig.FormatId(VendorId, ProductId);

        /// <summary>
        /// Whether QEMU can open the device. Its libusb only reaches devices driven by WinUSB (as the
        /// driver, or as a lower filter, which libusb accepts too); for any other driver the open fails
        /// with "unrecognized device driver" and QEMU retries every two seconds without a word, while
        /// the guest sees an empty "USB Host Device". Checked with libusb's own trace.
        /// </summary>
        public bool IsReachable =>
            IsWinUsb(Service) || LowerFilters.Any(IsWinUsb);

        /// <summary>A hub only carries other devices; handing it over would take them all from the host.</summary>
        public bool IsHub => Service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase)
                             || Service.Equals("usbhub", StringComparison.OrdinalIgnoreCase);

        private static bool IsWinUsb(string driver) => driver.Equals("WinUSB", StringComparison.OrdinalIgnoreCase);

        [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\", RegexOptions.IgnoreCase)]
        private static partial Regex DeviceIdPattern();

        /// <summary>
        /// Vendor and product from an instance id such as <c>USB\VID_0781&amp;PID_559C\4C53…</c>. Null for
        /// root hubs, and for the per-interface children of a composite device (<c>&amp;MI_00</c>), which
        /// are not devices of their own.
        /// </summary>
        public static (int VendorId, int ProductId)? ParseInstanceId(string instanceId)
        {
            var match = DeviceIdPattern().Match(instanceId);
            if (!match.Success)
                return null;

            return (int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
    }
}
