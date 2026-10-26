using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Validation, which exists to refuse configurations QEMU would accept and then fail to run — the
/// failures that leave a black console and nothing in the log.
/// </summary>
public class ValidationTests : IDisposable
{
    private readonly string _folder;

    public ValidationTests()
    {
        // Validation checks that the images exist, so the fixtures need real files.
        _folder = Path.Combine(Path.GetTempPath(), "winqemu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "disk0.qcow2"), "");
        File.WriteAllText(Path.Combine(_folder, "linux.iso"), "");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private VmConfig Vm()
    {
        var vm = Fixtures.Vm();
        vm.FolderPath = _folder;
        return vm;
    }

    private static IReadOnlyList<string> Validate(VmConfig vm, QemuInstallation? installation = null) =>
        QemuArgumentBuilder.Validate(vm, installation ?? Fixtures.Installation());

    [Fact]
    public void A_sound_configuration_reports_nothing()
    {
        Assert.Empty(Validate(Vm()));
    }

    private VmConfig VmWithForward(int hostPort, int guestPort)
    {
        var vm = Vm();
        var nat = new NetworkAdapterConfig { Id = "net0", Mode = NetworkMode.Nat };
        nat.PortForwards.Add(new PortForward { Protocol = PortForwardProtocol.Tcp, HostPort = hostPort, GuestPort = guestPort });
        vm.NetworkAdapters.Clear();
        vm.NetworkAdapters.Add(nat);
        return vm;
    }

    [Theory]
    [InlineData(2222, 0)]   // The guest port of a row whose edit was never committed.
    [InlineData(0, 22)]
    [InlineData(2222, 70000)]
    public void A_port_forward_outside_1_to_65535_is_refused(int hostPort, int guestPort)
    {
        // QEMU would only say "Invalid host forwarding rule (Bad guest port)" and exit.
        Assert.Contains(Validate(VmWithForward(hostPort, guestPort)), p => p.Contains("redirection de port", StringComparison.Ordinal));
    }

    [Fact]
    public void A_complete_port_forward_is_accepted()
    {
        Assert.Empty(Validate(VmWithForward(2222, 22)));
    }

    [Fact]
    public void The_cpu_models_that_never_execute_under_whpx_are_refused()
    {
        // Observed with QEMU 11 on WHPX: the guest runs no instruction at all, not even the firmware,
        // and nothing is logged. Refused up front rather than left as a blank screen.
        foreach (var model in new[] { "max", "host", "max,+aes" })
        {
            var vm = Vm();
            vm.CpuModel = model;

            Assert.Contains(Validate(vm), p => p.Contains("WHPX", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Those_same_models_are_fine_under_emulation()
    {
        var vm = Vm();
        vm.CpuModel = "max";
        vm.Accelerator = AcceleratorType.Tcg;

        Assert.Empty(Validate(vm));
    }

    [Fact]
    public void Secure_boot_is_refused_under_whpx_because_it_needs_smm()
    {
        // WHPX answers "System Management Mode not supported by this hypervisor" and QEMU exits.
        var vm = Vm();
        vm.Firmware = FirmwareType.UefiSecureBoot;

        Assert.Contains(Validate(vm), p => p.Contains("SMM", StringComparison.Ordinal));
    }

    [Fact]
    public void Secure_boot_is_allowed_under_emulation()
    {
        var vm = Vm();
        vm.Firmware = FirmwareType.UefiSecureBoot;
        vm.Accelerator = AcceleratorType.Tcg;

        Assert.Empty(Validate(vm));
    }

    [Fact]
    public void Missing_uefi_firmware_is_reported_instead_of_throwing_at_launch()
    {
        Assert.Contains(
            Validate(Vm(), Fixtures.Installation(hasUefiX64: false)),
            p => p.Contains("UEFI", StringComparison.Ordinal));
    }

    [Fact]
    public void A_guest_architecture_without_an_emulator_is_reported()
    {
        var vm = Vm();
        vm.Architecture = GuestArchitecture.Aarch64;

        Assert.Contains(
            Validate(vm, Fixtures.Installation(hasAarch64: false)),
            p => p.Contains("qemu-system-aarch64.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void More_sata_devices_than_the_chipset_has_ports_is_refused()
    {
        // The ICH9 AHCI controller on q35 exposes six ports, shared by disks and optical drives.
        var vm = Vm();
        vm.Disks[0].Bus = DiskBus.Sata;
        for (var i = 1; i <= 5; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"disk{i}.qcow2"), "");
            vm.Disks.Add(new DiskConfig { Id = $"disk{i}", FileName = $"disk{i}.qcow2", Bus = DiskBus.Sata });
        }
        vm.IsoPath = "linux.iso";

        Assert.Contains(Validate(vm), p => p.Contains("q35", StringComparison.Ordinal));
    }

    [Fact]
    public void The_always_present_optical_drive_counts_against_the_sata_budget()
    {
        // Six SATA disks and no ISO used to be legal. The empty tray takes the seventh port, so it is
        // not any more — a real change, and the alternative was a drive that cannot be filled later.
        var vm = Vm();
        vm.Disks[0].Bus = DiskBus.Sata;
        for (var i = 1; i <= 5; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"disk{i}.qcow2"), "");
            vm.Disks.Add(new DiskConfig { Id = $"disk{i}", FileName = $"disk{i}.qcow2", Bus = DiskBus.Sata });
        }

        Assert.Contains(Validate(vm), p => p.Contains("q35", StringComparison.Ordinal));
    }

    [Fact]
    public void Six_sata_devices_still_fit()
    {
        var vm = Vm();
        vm.Disks[0].Bus = DiskBus.Sata;
        for (var i = 1; i <= 4; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"disk{i}.qcow2"), "");
            vm.Disks.Add(new DiskConfig { Id = $"disk{i}", FileName = $"disk{i}.qcow2", Bus = DiskBus.Sata });
        }
        vm.IsoPath = "linux.iso";

        Assert.Empty(Validate(vm));
    }

    [Fact]
    public void Virtio_disks_do_not_consume_sata_ports()
    {
        var vm = Vm();
        for (var i = 1; i <= 8; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"disk{i}.qcow2"), "");
            vm.Disks.Add(new DiskConfig { Id = $"disk{i}", FileName = $"disk{i}.qcow2", Bus = DiskBus.VirtioBlk });
        }

        Assert.Empty(Validate(vm));
    }

    [Fact]
    public void Two_disks_sharing_an_identifier_are_refused()
    {
        // The id becomes a QEMU node name; a duplicate makes the command line ambiguous.
        var vm = Vm();
        vm.Disks.Add(new DiskConfig { Id = "disk0", FileName = "disk0.qcow2" });

        Assert.Contains(Validate(vm), p => p.Contains("identifier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_machine_with_nothing_to_boot_is_refused()
    {
        var vm = Vm();
        vm.Disks.Clear();

        Assert.Contains(Validate(vm), p => p.Contains("nothing to boot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_disk_image_is_reported_with_its_path()
    {
        var vm = Vm();
        vm.Disks[0].FileName = "absent.qcow2";

        Assert.Contains(Validate(vm), p => p.Contains("absent.qcow2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_iso_is_reported()
    {
        var vm = Vm();
        vm.IsoPath = "absent.iso";

        Assert.Contains(Validate(vm), p => p.Contains("absent.iso", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_machine_needs_at_least_one_cpu(int count)
    {
        var vm = Vm();
        vm.CpuCount = count;

        Assert.Contains(Validate(vm), p => p.Contains("CPU count", StringComparison.Ordinal));
    }

    [Fact]
    public void A_machine_needs_a_usable_amount_of_memory()
    {
        var vm = Vm();
        vm.MemoryMb = 64;

        Assert.Contains(Validate(vm), p => p.Contains("128", StringComparison.Ordinal));
    }
}
