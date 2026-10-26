using System.Globalization;
using System.Text.Json.Serialization;

namespace WinQEMU.Core.Models
{
    /// <summary>
    /// A host USB device handed to the guest from the start, and again whenever it is plugged in while
    /// the machine runs. Only the ones meant for every start are recorded; a device connected from the
    /// running machine's menu lasts until it is disconnected or the machine stops.
    /// </summary>
    /// <remarks>
    /// Identified by vendor and product id, which is all QEMU's usb-host can match on Windows besides a
    /// bus address that changes on every replug. Two identical devices plugged at once cannot be told
    /// apart: QEMU takes the first one it finds.
    /// </remarks>
    public class UsbDeviceConfig : ObservableBase
    {
        private int _vendorId;
        public int VendorId { get => _vendorId; set => SetProperty(ref _vendorId, value); }

        private int _productId;
        public int ProductId { get => _productId; set => SetProperty(ref _productId, value); }

        private string _name = string.Empty;
        /// <summary>The name the device reported when it was chosen, shown while it is unplugged.</summary>
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        /// <summary>"0781:559c", the form lsusb and QEMU's own listings use.</summary>
        [JsonIgnore]
        public string IdText => FormatId(VendorId, ProductId);

        public bool Matches(int vendorId, int productId) => VendorId == vendorId && ProductId == productId;

        public static string FormatId(int vendorId, int productId) =>
            $"{vendorId.ToString("x4", CultureInfo.InvariantCulture)}:{productId.ToString("x4", CultureInfo.InvariantCulture)}";
    }
}
