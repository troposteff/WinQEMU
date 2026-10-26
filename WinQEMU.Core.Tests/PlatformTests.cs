using System.Runtime.InteropServices;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Accelerator, machine type, CPU and the USB controller — the flags that decide whether the guest
/// executes at all. Every assertion here corresponds to a way a VM was once silently unbootable.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void Whpx_disables_the_kernel_irqchip()
    {
        // QEMU refuses to start with the default irqchip under WHPX, so the suffix is not optional.
        var args = Fixtures.Build(Fixtures.Vm());

        Assert.Equal("whpx,kernel-irqchip=off", Fixtures.Value(args, "-accel"));
    }

    [Fact]
    public void Whpx_turns_a_reboot_into_a_shutdown()
    {
        // An in-place reset never comes back from UEFI firmware under WHPX: QEMU stops the VM with
        // "Unexpected VP exit code 4". The session restarts the process instead.
        var args = Fixtures.Build(Fixtures.Vm());

        Assert.Equal("reboot=shutdown", Fixtures.Value(args, "-action"));
    }

    [Fact]
    public void Tcg_keeps_the_in_place_reset()
    {
        var vm = Fixtures.Vm();
        vm.Accelerator = AcceleratorType.Tcg;

        Assert.DoesNotContain("-action", Fixtures.Build(vm));
    }

    [Fact]
    public void Falls_back_to_tcg_when_whpx_is_unavailable()
    {
        var args = Fixtures.Build(Fixtures.Vm(), Fixtures.Installation(supportsWhpx: false));

        Assert.Equal("tcg,thread=multi", Fixtures.Value(args, "-accel"));
    }

    [Fact]
    public void Explicit_tcg_is_honoured_even_when_whpx_is_available()
    {
        var vm = Fixtures.Vm();
        vm.Accelerator = AcceleratorType.Tcg;

        Assert.Equal("tcg,thread=multi", Fixtures.Value(Fixtures.Build(vm), "-accel"));
    }

    [Fact]
    public void Explicit_whpx_survives_a_negative_probe()
    {
        // A false negative in detection must not lock the user out of acceleration.
        var vm = Fixtures.Vm();
        vm.Accelerator = AcceleratorType.Whpx;

        var args = Fixtures.Build(vm, Fixtures.Installation(supportsWhpx: false));

        Assert.Equal("whpx,kernel-irqchip=off", Fixtures.Value(args, "-accel"));
    }

    [Fact]
    public void A_foreign_guest_architecture_forces_emulation()
    {
        // Skipped on ARM64 hosts, where aarch64 is the native architecture and the rule inverts.
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            return;

        var vm = Fixtures.Vm();
        vm.Architecture = GuestArchitecture.Aarch64;

        Assert.Equal("tcg,thread=multi", Fixtures.Value(Fixtures.Build(vm), "-accel"));
    }

    [Fact]
    public void Cpu_model_is_passed_through_verbatim_under_tcg()
    {
        // Never rewritten to "max": under WHPX that model leaves the guest completely mute.
        var vm = Fixtures.Vm();
        vm.Accelerator = AcceleratorType.Tcg;
        vm.CpuModel = "qemu64,+sse4.1,+aes";

        Assert.Equal("qemu64,+sse4.1,+aes", Fixtures.Value(Fixtures.Build(vm), "-cpu"));
    }

    [Fact]
    public void Whpx_declares_the_xsave_state_that_carries_cet()
    {
        // WHPX passes the host's shadow-stack bit through regardless of the model. Without XSAVES and the
        // CET components, Linux guests never save the shadow-stack pointer, and processes on the same CPU
        // kill each other until systemd dies with "Attempted to kill init! exitcode=0x0000000b".
        var vm = Fixtures.Vm();
        vm.CpuModel = "qemu64,+sse4.1,+aes";

        Assert.Equal(
            "qemu64,+sse4.1,+aes,+xsave,+xsavec,+xsaves,+cet-ss,+cet-ibt",
            Fixtures.Value(Fixtures.Build(vm), "-cpu"));
    }

    [Fact]
    public void Whpx_keeps_cet_flags_the_user_already_set()
    {
        // An explicit choice, either way, is not second-guessed — and not duplicated.
        var vm = Fixtures.Vm();
        vm.CpuModel = "qemu64,+xsave,-cet-ss";

        Assert.Equal(
            "qemu64,+xsave,-cet-ss,+xsavec,+xsaves,+cet-ibt",
            Fixtures.Value(Fixtures.Build(vm), "-cpu"));
    }

    [Fact]
    public void Default_cpu_model_gets_consistent_cet_state_under_whpx()
    {
        // The default model already carries +xsave; only what is missing is appended.
        var cpu = Fixtures.Value(Fixtures.Build(Fixtures.Vm()), "-cpu");

        Assert.Equal("qemu64,+sse4.1,+sse4.2,+popcnt,+aes,+ssse3,+xsave,+xsavec,+xsaves,+cet-ss,+cet-ibt", cpu);
    }

    [Fact]
    public void Machine_type_is_q35_on_x86_and_virt_on_arm()
    {
        var x86 = Fixtures.Vm();
        Assert.Equal("q35", Fixtures.Value(Fixtures.Build(x86), "-machine"));

        var arm = Fixtures.Vm();
        arm.Architecture = GuestArchitecture.Aarch64;
        // q35 is meaningless on ARM, so the stored default is replaced rather than passed on.
        Assert.Equal("virt", Fixtures.Value(Fixtures.Build(arm), "-machine"));
    }

    [Fact]
    public void Secure_boot_asks_the_machine_for_smm()
    {
        var vm = Fixtures.Vm();
        vm.Firmware = FirmwareType.UefiSecureBoot;
        vm.Accelerator = AcceleratorType.Tcg;

        Assert.Equal("q35,smm=on", Fixtures.Value(Fixtures.Build(vm), "-machine"));
    }

    [Fact]
    public void Smp_stays_a_bare_count_until_a_topology_is_requested()
    {
        var vm = Fixtures.Vm();
        vm.CpuCount = 4;
        Assert.Equal("4", Fixtures.Value(Fixtures.Build(vm), "-smp"));

        vm.CoresPerSocket = 2;
        Assert.Equal("cpus=4,cores=2,sockets=2,threads=1", Fixtures.Value(Fixtures.Build(vm), "-smp"));
    }

    [Fact]
    public void Rtc_follows_the_guest_convention()
    {
        var vm = Fixtures.Vm();
        vm.RtcLocalTime = true;
        Assert.Equal("base=localtime", Fixtures.Value(Fixtures.Build(vm), "-rtc"));

        vm.RtcLocalTime = false;
        Assert.Equal("base=utc", Fixtures.Value(Fixtures.Build(vm), "-rtc"));
    }

    [Fact]
    public void Usb_controller_is_declared_before_anything_plugged_into_it()
    {
        // Order matters: a device whose bus does not exist yet is rejected by QEMU.
        var vm = Fixtures.Vm();
        vm.EnableUsbTablet = true;

        var args = Fixtures.Build(vm);
        var controller = Fixtures.IndexOf(args, "-usb");
        var tablet = Fixtures.IndexOf(args, "usb-tablet");

        Assert.True(controller >= 0, "the -usb controller is missing");
        Assert.True(tablet >= 0, "the usb-tablet device is missing");
        Assert.True(controller < tablet, $"-usb at {controller} must precede usb-tablet at {tablet}");
    }

    [Fact]
    public void Arm_gets_an_xhci_controller_because_virt_has_no_built_in_usb()
    {
        var vm = Fixtures.Vm();
        vm.Architecture = GuestArchitecture.Aarch64;
        vm.EnableUsbTablet = true;

        var args = Fixtures.Build(vm);

        Assert.DoesNotContain("-usb", args);
        Assert.Equal("qemu-xhci,id=xhci", Fixtures.Device(args, "qemu-xhci"));
    }

    [Fact]
    public void An_arm_optical_drive_brings_the_controller_even_without_a_tablet()
    {
        var vm = Fixtures.Vm();
        vm.Architecture = GuestArchitecture.Aarch64;
        vm.EnableUsbTablet = false;
        vm.IsoPath = @"C:\iso\linux.iso";

        var args = Fixtures.Build(vm);
        var controller = Fixtures.IndexOfContaining(args, "qemu-xhci");
        var storage = Fixtures.IndexOfContaining(args, "usb-storage");

        Assert.True(controller >= 0 && storage >= 0);
        Assert.True(controller < storage);
    }

    [Fact]
    public void Uefi_emits_a_read_only_code_bank_and_a_private_variable_store()
    {
        var vm = Fixtures.Vm();
        var args = Fixtures.Build(vm);
        var pflash = Fixtures.Values(args, "-drive").Where(d => d.StartsWith("if=pflash", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, pflash.Count);
        Assert.Contains("unit=0", pflash[0]);
        Assert.Contains("readonly=on", pflash[0]);
        Assert.Contains("unit=1", pflash[1]);
        // The variable store must be the VM's own copy, or boot entries leak between machines.
        Assert.Contains(@"C:\qemu\Test\firmware-vars.fd", pflash[1]);
        Assert.DoesNotContain("readonly=on", pflash[1]);
    }

    [Fact]
    public void Bios_emits_no_pflash_at_all()
    {
        var vm = Fixtures.Vm();
        vm.Firmware = FirmwareType.Bios;

        Assert.DoesNotContain(Fixtures.Values(Fixtures.Build(vm), "-drive"), d => d.StartsWith("if=pflash", StringComparison.Ordinal));
    }

    [Fact]
    public void Qmp_listens_on_loopback_without_waiting_for_a_client()
    {
        var args = Fixtures.Build(Fixtures.Vm(), qmpPort: 5555);

        Assert.Equal("tcp:127.0.0.1:5555,server=on,wait=off", Fixtures.Value(args, "-qmp"));
    }
}
