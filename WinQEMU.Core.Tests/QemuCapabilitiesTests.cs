using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Parsing what QEMU says it supports. The samples are real output from qemu-system-x86_64 11.1.0,
/// trimmed; the shape of these lists is what the dropdowns are built from, and a name picked up wrong
/// would be offered to the user and then rejected by QEMU at launch.
/// </summary>
public class QemuCapabilitiesTests
{
    private const string MachineHelp = """
        Supported machines are:
        microvm              microvm (i386)
        pc-i440fx-9.2        Standard PC (i440FX + PIIX, 1996)
        pc-i440fx-8.1        Standard PC (i440FX + PIIX, 1996) (deprecated)
        pc                   Standard PC (i440FX + PIIX, 1996) (alias of pc-i440fx-9.2)
        q35                  Standard PC (Q35 + ICH9, 2009) (alias of pc-q35-9.2)
        pc-q35-9.2           Standard PC (Q35 + ICH9, 2009)
        none                 empty machine
        """;

    private const string CpuHelp = """
        Available CPUs:
          486                   (alias configured by machine type)
          486-v1
          Broadwell             (alias configured by machine type)
          Broadwell-IBRS        (alias of Broadwell-v3)
          qemu64-v1
          host                  (alias configured by machine type)
          max

        Recognized CPUID flags:
          3dnow 3dnowext 3dnowprefetch abm ace2 ace2-en acpi adx aes amd-no-ssb
          apic arat arch-capabilities avx avx2 avx512-4fmaps avx512-4vnniw
        """;

    [Fact]
    public void Machine_names_are_taken_without_their_descriptions()
    {
        var machines = QemuCapabilities.ParseMachineTypes(MachineHelp);

        Assert.Equal(
            new[] { "microvm", "pc-i440fx-9.2", "pc-i440fx-8.1", "pc", "q35", "pc-q35-9.2", "none" },
            machines);
    }

    [Fact]
    public void The_machine_heading_is_not_mistaken_for_a_machine()
    {
        Assert.DoesNotContain("Supported", QemuCapabilities.ParseMachineTypes(MachineHelp));
    }

    [Fact]
    public void Machines_keep_the_order_qemu_lists_them_in()
    {
        // QEMU groups them by family, which is more useful than alphabetical: the q35 variants stay
        // together and the plain aliases sit next to the versions they point at.
        var machines = QemuCapabilities.ParseMachineTypes(MachineHelp);

        Assert.True(
            machines.ToList().IndexOf("pc") < machines.ToList().IndexOf("q35"),
            "the listing order was not preserved");
    }

    [Fact]
    public void Cpu_models_are_taken_without_their_alias_notes()
    {
        var cpus = QemuCapabilities.ParseCpuModels(CpuHelp);

        Assert.Equal(
            new[] { "486", "486-v1", "Broadwell", "Broadwell-IBRS", "qemu64-v1", "host", "max" },
            cpus);
    }

    [Fact]
    public void The_cpuid_flag_section_is_not_read_as_models()
    {
        // The flags follow the models in the same output; taking them for models would fill the dropdown
        // with hundreds of names QEMU refuses as a -cpu value.
        var cpus = QemuCapabilities.ParseCpuModels(CpuHelp);

        Assert.DoesNotContain("3dnow", cpus);
        Assert.DoesNotContain("apic", cpus);
        Assert.DoesNotContain("Recognized", cpus);
    }

    [Fact]
    public void The_models_that_do_not_run_under_whpx_are_still_offered()
    {
        // Listing them is right — they work under emulation — and the validator is what refuses the
        // combination, with an explanation. Hiding them would silently disagree with QEMU.
        var cpus = QemuCapabilities.ParseCpuModels(CpuHelp);

        Assert.Contains("max", cpus);
        Assert.Contains("host", cpus);
    }

    [Fact]
    public void Empty_output_yields_empty_lists_rather_than_throwing()
    {
        // A build that prints nothing, or a probe that failed, must leave the dropdowns empty and
        // editable rather than break the section.
        Assert.Empty(QemuCapabilities.ParseMachineTypes(string.Empty));
        Assert.Empty(QemuCapabilities.ParseCpuModels("   \n \n"));
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        var machines = QemuCapabilities.ParseMachineTypes("Supported machines are:\r\nq35   Standard PC\r\npc  Older PC\r\n");

        Assert.Equal(new[] { "q35", "pc" }, machines);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task The_real_binary_offers_q35_and_qemu64()
    {
        // Guards the parser against a change in QEMU's own output format: these two names have to survive
        // the round trip through the real help text.
        var detection = await QemuLocator.DetectAsync();
        if (detection.Installation is null)
            return;

        var capabilities = await QemuCapabilities.ProbeAsync(detection.Installation, GuestArchitecture.X86_64);

        Assert.Contains("q35", capabilities.MachineTypes);
        Assert.Contains("qemu64", capabilities.CpuModels);
        // A real list, not one line mistaken for many.
        Assert.True(capabilities.MachineTypes.Count > 5, $"only {capabilities.MachineTypes.Count} machines parsed");
        Assert.True(capabilities.CpuModels.Count > 20, $"only {capabilities.CpuModels.Count} models parsed");
        // And no flag leaked in from the second section.
        Assert.DoesNotContain("3dnow", capabilities.CpuModels);
    }
}
