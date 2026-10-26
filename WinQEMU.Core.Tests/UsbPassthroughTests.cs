using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Usb;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Host USB devices handed to a guest: QEMU's usb-host on its own xHCI controller, and the Windows side
/// that tells which devices QEMU can open at all.
/// </summary>
public class UsbPassthroughTests
{
    private static IReadOnlyList<string> BuildWithClipboard(VmConfig config) =>
        QemuArgumentBuilder.Build(config, Fixtures.Installation(), 4444, 50000);

    [Fact]
    public void Every_machine_gets_a_usb3_controller_for_host_devices()
    {
        var devices = Fixtures.Values(Fixtures.Build(Fixtures.Vm()), "-device");

        Assert.Contains("qemu-xhci,id=wq-usb", devices);
        Assert.DoesNotContain(devices, d => d.StartsWith("usb-host", StringComparison.Ordinal));
    }

    [Fact]
    public void The_controller_comes_after_every_other_pci_device()
    {
        // Added to existing machines: placed anywhere else it would take the slot of the device after it,
        // and a Linux guest would rename its network card (enp0s3 -> enp0s4) and lose its configuration.
        var config = Fixtures.Vm();
        config.NetworkAdapters.Add(new NetworkAdapterConfig());
        var devices = Fixtures.Values(BuildWithClipboard(config), "-device");

        var controller = devices.IndexOf("qemu-xhci,id=wq-usb");
        Assert.Equal(devices.Count - 1, controller);
        Assert.Contains(devices.Take(controller), d => d.StartsWith("virtio-serial", StringComparison.Ordinal));
        Assert.Contains(devices.Take(controller), d => d.StartsWith("virtio-net", StringComparison.Ordinal));
    }

    [Fact]
    public void Devices_recorded_for_every_start_plug_into_that_controller()
    {
        var config = Fixtures.Vm();
        config.UsbDevices.Add(new UsbDeviceConfig { VendorId = 0x0781, ProductId = 0x559C, Name = "Ultra USB 3.0" });
        // The same device twice would be a duplicate id, and QEMU would refuse to start.
        config.UsbDevices.Add(new UsbDeviceConfig { VendorId = 0x0781, ProductId = 0x559C });
        var devices = Fixtures.Values(Fixtures.Build(config), "-device");

        var host = Assert.Single(devices, d => d.StartsWith("usb-host", StringComparison.Ordinal));
        Assert.Equal("usb-host,id=wq-usb-0781-559c,bus=wq-usb.0,vendorid=0x0781,productid=0x559c", host);
        Assert.True(devices.IndexOf(host) > devices.IndexOf("qemu-xhci,id=wq-usb"));
    }

    [Fact]
    public void A_device_id_gives_back_its_vendor_and_product()
    {
        var id = QemuArgumentBuilder.UsbHostDeviceId(0x27C6, 0x633C);

        Assert.Equal((0x27C6, 0x633C), VmSession.ParseUsbHostDeviceId(id));
        Assert.Null(VmSession.ParseUsbHostDeviceId(QemuArgumentBuilder.UsbPassthroughControllerId));
        Assert.Null(VmSession.ParseUsbHostDeviceId("net0"));
    }

    [Theory]
    [InlineData(@"USB\VID_0781&PID_559C\4C530001241026109142", 0x0781, 0x559C)]
    [InlineData(@"USB\VID_8087&PID_0033\5&356BEDF6&0&10", 0x8087, 0x0033)]
    public void A_windows_instance_id_gives_vendor_and_product(string instanceId, int vendor, int product)
    {
        Assert.Equal((vendor, product), HostUsbDevice.ParseInstanceId(instanceId));
    }

    [Theory]
    [InlineData(@"USB\ROOT_HUB30\4&1033D8A1&0&0")]
    [InlineData(@"USB\VID_046D&PID_C52B&MI_00\7&2A1D39AB&0&0000")]   // one interface of a composite device
    [InlineData(@"HID\VID_8087&PID_0AC2\7&17638906&0&0000")]
    public void Hubs_interfaces_and_other_buses_are_not_devices_to_hand_over(string instanceId)
    {
        Assert.Null(HostUsbDevice.ParseInstanceId(instanceId));
    }

    [Theory]
    [InlineData("WinUSB", "", true)]
    [InlineData("WUDFRd", "WinUsb", true)]   // a fingerprint reader: libusb accepts WinUSB as a lower filter
    [InlineData("USBSTOR", "", false)]
    [InlineData("BTHUSB", "ibtusb", false)]
    public void Only_a_device_on_winusb_can_be_opened_by_qemu(string service, string filter, bool reachable)
    {
        var device = new HostUsbDevice
        {
            InstanceId = @"USB\VID_0781&PID_559C\1",
            VendorId = 0x0781,
            ProductId = 0x559C,
            Name = "Ultra USB 3.0",
            Service = service,
            LowerFilters = filter.Length > 0 ? new[] { filter } : Array.Empty<string>()
        };

        Assert.Equal(reachable, device.IsReachable);
    }
}

/// <summary>The SetupAPI walk, on whatever this machine has plugged in.</summary>
[Trait("Category", "Integration")]
public class HostUsbEnumeratorTests
{
    [Fact]
    public void Lists_each_plugged_device_once_with_a_name_and_a_driver()
    {
        var devices = HostUsbEnumerator.List();

        Assert.All(devices, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.NotEqual(0, d.VendorId);
            Assert.False(d.IsHub);
        });
        Assert.Equal(devices.Count, devices.Select(d => d.InstanceId).Distinct().Count());
    }
}
