using System.Runtime.InteropServices;
using System.Text;

namespace WinQEMU.Core.Usb
{
    /// <summary>
    /// Lists the USB devices plugged into the host through SetupAPI, with the driver each one runs on.
    /// </summary>
    /// <remarks>
    /// Asked of Windows rather than of QEMU (<c>info usbhost</c>): it works with no machine running, it
    /// gives the product name and, above all, the driver, which decides whether QEMU can open the device
    /// at all (<see cref="HostUsbDevice.IsReachable"/>).
    ///
    /// Walks the GUID_DEVINTERFACE_USB_DEVICE interface, which the hub driver registers for every device
    /// it enumerates whatever the function driver — the same pass libusb makes — so each physical device
    /// appears once, and the interfaces of a composite device do not.
    /// </remarks>
    public static class HostUsbEnumerator
    {
        private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        /// <summary>
        /// DEVPKEY_Device_BusReportedDeviceDesc: the product string the device itself reports ("Ultra USB
        /// 3.0"), where the friendly name is the driver's ("Dispositif de stockage de masse USB").
        /// </summary>
        private static readonly DevPropKey BusReportedDescription =
            new() { FormatId = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), PropertyId = 4 };

        public static IReadOnlyList<HostUsbDevice> List()
        {
            var devices = new List<HostUsbDevice>();
            var interfaceGuid = UsbDeviceInterface;
            var set = SetupDiGetClassDevsW(ref interfaceGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == InvalidHandle)
                return devices;

            try
            {
                var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref info); index++)
                {
                    var instanceId = InstanceId(set, ref info);
                    if (instanceId is null || HostUsbDevice.ParseInstanceId(instanceId) is not { } ids)
                        continue;

                    var name = FirstNonEmpty(
                        DeviceProperty(set, ref info, BusReportedDescription),
                        RegistryStrings(set, ref info, SpdrpFriendlyName).FirstOrDefault(),
                        RegistryStrings(set, ref info, SpdrpDeviceDesc).FirstOrDefault(),
                        "Périphérique USB");

                    devices.Add(new HostUsbDevice
                    {
                        InstanceId = instanceId,
                        VendorId = ids.VendorId,
                        ProductId = ids.ProductId,
                        Name = name,
                        Service = RegistryStrings(set, ref info, SpdrpService).FirstOrDefault() ?? string.Empty,
                        LowerFilters = RegistryStrings(set, ref info, SpdrpLowerFilters)
                    });
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return devices.Where(d => !d.IsHub).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.First(v => !string.IsNullOrWhiteSpace(v))!.Trim();

        private static string? InstanceId(IntPtr set, ref SpDevInfoData info)
        {
            var buffer = new char[512];
            return SetupDiGetDeviceInstanceIdW(set, ref info, buffer, buffer.Length, out var required)
                ? new string(buffer, 0, Math.Max(0, required - 1))
                : null;
        }

        /// <summary>A REG_SZ or REG_MULTI_SZ device property, split into its strings.</summary>
        private static IReadOnlyList<string> RegistryStrings(IntPtr set, ref SpDevInfoData info, uint property)
        {
            SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, null, 0, out var required);
            if (required <= 0)
                return Array.Empty<string>();

            var buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, buffer.Length, out _))
                return Array.Empty<string>();

            return Encoding.Unicode.GetString(buffer)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static string? DeviceProperty(IntPtr set, ref SpDevInfoData info, DevPropKey key)
        {
            SetupDiGetDevicePropertyW(set, ref info, ref key, out _, null, 0, out var required, 0);
            if (required <= 0)
                return null;

            var buffer = new byte[required];
            return SetupDiGetDevicePropertyW(set, ref info, ref key, out _, buffer, buffer.Length, out _, 0)
                ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
                : null;
        }

        private const uint DigcfPresent = 0x2;
        private const uint DigcfDeviceInterface = 0x10;
        private const uint SpdrpDeviceDesc = 0x0;
        private const uint SpdrpService = 0x4;
        private const uint SpdrpFriendlyName = 0xC;
        private const uint SpdrpLowerFilters = 0x12;
        private static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevInfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData info);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr set, ref SpDevInfoData info, char[] buffer, int bufferSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr set, ref SpDevInfoData info, uint property, out uint registryType,
            byte[]? buffer, int bufferSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(
            IntPtr set, ref SpDevInfoData info, ref DevPropKey key, out uint propertyType,
            byte[]? buffer, int bufferSize, out int requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
