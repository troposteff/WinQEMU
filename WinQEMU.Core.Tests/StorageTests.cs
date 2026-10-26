using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

public class StorageTests
{
    [Fact]
    public void A_virtio_disk_becomes_a_pci_block_device()
    {
        var args = Fixtures.Build(Fixtures.Vm());

        Assert.Equal("virtio-blk-pci,drive=disk0,id=disk0-dev,bootindex=0", Fixtures.Device(args, "virtio-blk-pci"));
    }

    [Fact]
    public void Sata_and_ide_disks_share_the_ahci_bus_and_each_take_a_port()
    {
        // q35 exposes its AHCI controller as the ide bus; two disks must not land on the same unit.
        var vm = Fixtures.Vm();
        vm.Disks[0].Bus = DiskBus.Sata;
        vm.Disks.Add(new DiskConfig { Id = "disk1", FileName = "disk1.qcow2", Bus = DiskBus.Ide });

        var devices = Fixtures.Values(Fixtures.Build(vm), "-device")
            .Where(d => d.StartsWith("ide-hd", StringComparison.Ordinal))
            .ToList();

        Assert.Contains("bus=ide.0", devices[0]);
        Assert.Contains("bus=ide.1", devices[1]);
    }

    [Fact]
    public void An_optical_drive_takes_the_next_ahci_port_after_the_disks()
    {
        var vm = Fixtures.Vm();
        vm.Disks[0].Bus = DiskBus.Sata;
        vm.IsoPath = @"C:\iso\linux.iso";

        var args = Fixtures.Build(vm);

        Assert.Contains("bus=ide.0", Fixtures.Device(args, "ide-hd")!);
        Assert.Contains("bus=ide.1", Fixtures.Device(args, "ide-cd")!);
    }

    [Fact]
    public void An_nvme_disk_carries_a_serial()
    {
        var vm = Fixtures.Vm();
        vm.Disks[0].Bus = DiskBus.Nvme;

        Assert.Contains("nvme,drive=disk0,serial=disk0", Fixtures.Device(Fixtures.Build(vm), "nvme")!);
    }

    [Fact]
    public void Disk_flags_reach_the_drive_definition()
    {
        var vm = Fixtures.Vm();
        vm.Disks[0].Format = DiskFormat.Raw;
        vm.Disks[0].Cache = DiskCacheMode.None;
        vm.Disks[0].ReadOnly = true;
        vm.Disks[0].Discard = true;

        var drive = Fixtures.Drive(Fixtures.Build(vm), "disk0")!;

        Assert.Contains("format=raw", drive);
        Assert.Contains("cache=none", drive);
        Assert.Contains("readonly=on", drive);
        Assert.Contains("discard=unmap", drive);
    }

    [Fact]
    public void Relative_disk_paths_are_resolved_against_the_vm_folder()
    {
        // What makes a VM folder portable: the descriptor stores disk0.qcow2, not an absolute path.
        var drive = Fixtures.Drive(Fixtures.Build(Fixtures.Vm()), "disk0")!;

        Assert.Contains(@"file=C:\qemu\Test\disk0.qcow2", drive);
    }

    [Fact]
    public void An_iso_is_a_real_optical_device_not_a_legacy_cdrom_drive()
    {
        // The legacy form (media=cdrom with an index) produced nothing usable on q35: that machine has
        // no legacy IDE controller, so the drive never appeared in the firmware device map.
        var vm = Fixtures.Vm();
        vm.IsoPath = @"C:\iso\linux.iso";

        var args = Fixtures.Build(vm);
        var drive = Fixtures.Drive(args, "cd0")!;

        Assert.Contains("media=cdrom", drive);
        Assert.Contains("readonly=on", drive);
        Assert.DoesNotContain("index=", drive);
        Assert.NotNull(Fixtures.Device(args, "ide-cd"));
    }

    [Fact]
    public void An_x86_machine_always_has_an_optical_drive_even_with_nothing_in_it()
    {
        // Without a device there is nothing for a live medium change to address, so a machine started
        // without an ISO could never be handed a driver disc afterwards.
        var args = Fixtures.Build(Fixtures.Vm());
        var drive = Fixtures.Drive(args, "cd0");

        Assert.NotNull(drive);
        Assert.Contains("media=cdrom", drive!);
        Assert.DoesNotContain("file=", drive);
        Assert.NotNull(Fixtures.Device(args, "ide-cd"));
    }

    [Fact]
    public void An_empty_tray_is_invisible_to_the_boot_manager()
    {
        // Only a drive holding something is ranked; an empty one must not offer itself for boot.
        var device = Fixtures.Device(Fixtures.Build(Fixtures.Vm()), "ide-cd")!;

        Assert.DoesNotContain("bootindex", device);
    }

    [Fact]
    public void An_arm_machine_gets_no_empty_tray()
    {
        // There the drive is usb-storage, which refuses to start without media.
        var vm = Fixtures.Vm();
        vm.Architecture = GuestArchitecture.Aarch64;

        var args = Fixtures.Build(vm);

        Assert.Null(Fixtures.Drive(args, "cd0"));
        Assert.Null(Fixtures.Device(args, "usb-storage"));
    }

    [Fact]
    public void The_optical_device_id_is_the_one_a_live_medium_change_targets()
    {
        // The regression this guards: the live CD swap addressed the legacy "ide1-cd0" while the builder
        // emitted an explicit ide-cd device, so changing the medium could only ever have failed. Both
        // sides now read the id from the same place, and this proves the emitted id really is that one.
        var vm = Fixtures.Vm();
        vm.IsoPath = @"C:\iso\linux.iso";

        var expected = QemuArgumentBuilder.DeviceIdFor(QemuArgumentBuilder.PrimaryCdId);

        Assert.Contains($"id={expected}", Fixtures.Device(Fixtures.Build(vm), "ide-cd")!);
    }

    [Fact]
    public void Disk_devices_are_named_the_same_way()
    {
        var expected = QemuArgumentBuilder.DeviceIdFor("disk0");

        Assert.Contains($"id={expected}", Fixtures.Device(Fixtures.Build(Fixtures.Vm()), "virtio-blk-pci")!);
    }

    [Fact]
    public void An_arm_optical_drive_is_usb_mass_storage()
    {
        var vm = Fixtures.Vm();
        vm.Architecture = GuestArchitecture.Aarch64;
        vm.IsoPath = @"C:\iso\linux.iso";

        var args = Fixtures.Build(vm);

        Assert.Null(Fixtures.Device(args, "ide-cd"));
        Assert.Contains("usb-storage,drive=cd0", Fixtures.Device(args, "usb-storage")!);
    }
}
