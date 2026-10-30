using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using Xunit;

namespace WinQEMU.Core.Tests;

public class CpuModelSpecTests
{
    [Fact]
    public void Choosing_another_model_keeps_the_options()
    {
        // The bug: picking "Skylake-Client" from the list dropped +sse4.1… that Windows 11 needs.
        Assert.Equal("Skylake-Client,+sse4.1,+sse4.2,+popcnt,+aes,+ssse3,+xsave",
            CpuModelSpec.WithModel(VmConfig.DefaultCpuModel, "Skylake-Client"));
    }

    [Fact]
    public void Flags_are_set_and_other_options_kept()
    {
        var value = CpuModelSpec.WithFlags("qemu64,+aes,-avx,level=13", new[] { "avx", "sse4.2" });

        Assert.Equal("qemu64,level=13,+avx,+sse4.2", value);
        Assert.Equal(new[] { "avx", "sse4.2" }, CpuModelSpec.EnabledFlags(value));
    }

    [Fact]
    public void An_empty_value_reads_as_the_default()
    {
        Assert.Equal("qemu64", CpuModelSpec.Model(""));
        Assert.Contains("sse4.2", CpuModelSpec.EnabledFlags(null));
    }

    [Fact]
    public void Cpu_flags_come_from_the_last_section_of_cpu_help()
    {
        var flags = QemuCapabilities.ParseCpuFlags("""
            Available CPUs:
              qemu64
            Recognized CPUID flags:
              3dnow aes avx
              aes sse4.2
            """);

        Assert.Equal(new[] { "3dnow", "aes", "avx", "sse4.2" }, flags);
    }
}
