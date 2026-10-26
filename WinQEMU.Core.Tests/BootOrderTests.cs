using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Boot order. UEFI ignores <c>-boot order=</c> entirely and only honours a per-device
/// <c>bootindex</c>; a device without one is invisible to its boot manager. That is what once made an
/// ISO impossible to boot, so these are the tests that matter most.
/// </summary>
public class BootOrderTests
{
    private static VmConfig WithIso()
    {
        var vm = Fixtures.Vm();
        vm.IsoPath = @"C:\iso\linux.iso";
        return vm;
    }

    /// <summary>Reads back the bootindex of one device id from the generated arguments.</summary>
    private static int? BootIndexOf(IReadOnlyList<string> args, string deviceId)
    {
        var device = Fixtures.Values(args, "-device").FirstOrDefault(v => v.Contains($"id={deviceId}-dev", StringComparison.Ordinal));
        if (device is null)
            return null;

        var marker = device.Split(',').FirstOrDefault(p => p.StartsWith("bootindex=", StringComparison.Ordinal));
        return marker is null ? null : int.Parse(marker["bootindex=".Length..]);
    }

    [Fact]
    public void The_optical_drive_always_gets_a_bootindex()
    {
        // Without one, UEFI never offers the ISO and the machine drops into the shell.
        var args = Fixtures.Build(WithIso());

        Assert.NotNull(BootIndexOf(args, "cd0"));
    }

    [Fact]
    public void Boot_from_iso_puts_the_optical_drive_first()
    {
        var vm = WithIso();
        vm.BootFromIso = true;

        var args = Fixtures.Build(vm);

        Assert.Equal(0, BootIndexOf(args, "cd0"));
        Assert.Equal(1, BootIndexOf(args, "disk0"));
    }

    [Fact]
    public void Otherwise_the_disk_comes_first_and_the_iso_second()
    {
        var vm = WithIso();
        vm.BootFromIso = false;
        vm.Disks[0].BootIndex = 0;

        var args = Fixtures.Build(vm);

        Assert.Equal(0, BootIndexOf(args, "disk0"));
        Assert.Equal(1, BootIndexOf(args, "cd0"));
    }

    [Fact]
    public void The_driver_iso_is_tried_last()
    {
        var vm = WithIso();
        vm.BootFromIso = true;
        vm.SecondaryIsoPath = @"C:\iso\virtio-win.iso";
        vm.Disks[0].BootIndex = 0;

        var args = Fixtures.Build(vm);

        Assert.Equal(0, BootIndexOf(args, "cd0"));
        Assert.Equal(1, BootIndexOf(args, "disk0"));
        Assert.Equal(2, BootIndexOf(args, "cd1"));
    }

    [Fact]
    public void An_explicit_boot_order_overrides_the_derived_one()
    {
        var vm = WithIso();
        vm.BootFromIso = true;                       // would put cd0 first
        vm.BootOrder.Add("disk0");                   // but the user ranked the disk above it
        vm.BootOrder.Add("cd0");

        var args = Fixtures.Build(vm);

        Assert.Equal(0, BootIndexOf(args, "disk0"));
        Assert.Equal(1, BootIndexOf(args, "cd0"));
    }

    [Fact]
    public void An_explicit_order_ignores_devices_that_no_longer_exist()
    {
        // A disk can be detached after the order was set; the stale id must not consume a rank.
        var vm = WithIso();
        vm.BootOrder.Add("disk-removed");
        vm.BootOrder.Add("cd0");
        vm.BootOrder.Add("disk0");

        var args = Fixtures.Build(vm);

        Assert.Equal(0, BootIndexOf(args, "cd0"));
        Assert.Equal(1, BootIndexOf(args, "disk0"));
    }

    [Fact]
    public void A_device_missing_from_an_explicit_order_is_still_given_a_rank()
    {
        // Appended rather than dropped: no bootindex at all means the firmware cannot see it.
        var vm = WithIso();
        vm.BootOrder.Add("cd0");

        var args = Fixtures.Build(vm);

        Assert.Equal(0, BootIndexOf(args, "cd0"));
        Assert.Equal(1, BootIndexOf(args, "disk0"));
    }

    [Fact]
    public void Boot_menu_is_offered_but_order_is_never_used()
    {
        // -boot order= is silently ignored by UEFI, so it must not be emitted as if it worked.
        var args = Fixtures.Build(WithIso());

        Assert.Equal("menu=on", Fixtures.Value(args, "-boot"));
        Assert.DoesNotContain(Fixtures.Values(args, "-boot"), v => v.Contains("order=", StringComparison.Ordinal));
    }

    [Fact]
    public void Enumerating_bootable_devices_lists_the_drives_the_firmware_can_try()
    {
        var vm = WithIso();
        vm.SecondaryIsoPath = @"C:\iso\virtio-win.iso";

        Assert.Equal(new[] { "cd0", "disk0", "cd1" }, QemuArgumentBuilder.EnumerateBootableIds(vm));
    }

    [Fact]
    public void A_disk_left_unranked_gets_no_bootindex_from_the_fallback()
    {
        // BootIndex -1 means "no stated preference". The firmware still finds the disk on its own; it
        // simply has no ordering hint. Ranking one device is what opts the machine into an explicit order.
        var vm = Fixtures.Vm();
        vm.Disks[0].BootIndex = -1;

        Assert.Null(BootIndexOf(Fixtures.Build(vm), "disk0"));
    }

    [Fact]
    public void An_unranked_disk_is_still_offered_in_the_boot_list()
    {
        // The shell lists it so the user can rank it; only the generated flags stay silent.
        var vm = Fixtures.Vm();
        vm.Disks[0].BootIndex = -1;

        Assert.Contains("disk0", QemuArgumentBuilder.EnumerateBootableIds(vm));
    }

    [Fact]
    public void An_empty_optical_drive_is_not_bootable()
    {
        Assert.Equal(new[] { "disk0" }, QemuArgumentBuilder.EnumerateBootableIds(Fixtures.Vm()));
    }
}
