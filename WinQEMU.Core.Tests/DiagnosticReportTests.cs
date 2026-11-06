using WinQEMU.Core.Models;
using WinQEMU.Core.Services;
using Xunit;

namespace WinQEMU.Core.Tests;

public class DiagnosticReportTests
{
    private static readonly HostDescription Host =
        new("Windows 10 Pro 25H2, build 26300.1", "AMD Ryzen 9 5900XT 16-Core Processor", 32, 64L << 30, "off");

    [Fact]
    public void Redact_hides_the_profile_account_and_pc_names()
    {
        var text = @"-drive file=C:\Users\Jean\Downloads\opensuse.iso user jean on JEAN-PC, jeanne stays";

        var redacted = DiagnosticReport.Redact(text, @"C:\Users\Jean", "jean", "JEAN-PC");

        Assert.Equal(@"-drive file=%USERPROFILE%\Downloads\opensuse.iso user <user> on <pc>, jeanne stays", redacted);
    }

    [Fact]
    public void Report_lists_the_pc_and_the_end_of_the_machine_log()
    {
        var machine = new VmConfig { Name = "openSUSE", GuestOs = "opensuse", CpuModel = string.Empty, CpuCount = 4 };
        var log = Enumerable.Range(1, DiagnosticReport.LogLines + 5).Select(i => $"line {i}").ToList();

        var report = DiagnosticReport.Build("2026.1.3", "Microsoft Store", Host, null,
            HypervisorPlatformState.Ready, machine, VmState.Running, log);

        Assert.Contains("- Processor: AMD Ryzen 9 5900XT 16-Core Processor, 32 logical processors", report);
        Assert.Contains("- Memory: 64.0 GiB", report);
        Assert.Contains("- QEMU: not found", report);
        Assert.Contains("- Name: openSUSE\n- Guest profile: opensuse, X86_64, Uefi", report);
        Assert.Contains($"- CPU: {VmConfig.DefaultCpuModel}, 4 vCPU", report);
        Assert.Contains($"### Log (last {DiagnosticReport.LogLines} lines)", report);
        Assert.DoesNotContain("line 5\n", report);
        Assert.Contains("line 6\n", report);
        Assert.EndsWith("```\n", report);
    }

    [Fact]
    public void Report_without_a_machine_has_no_log()
    {
        var report = DiagnosticReport.Build("2026.1.3", "GitHub", Host, null, HypervisorPlatformState.NotEnabled,
            null, null, Array.Empty<string>());

        Assert.Contains("- Windows Hypervisor Platform: NotEnabled", report);
        Assert.DoesNotContain("### Log", report);
    }
}
