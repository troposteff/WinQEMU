using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

public class HypervisorPlatformTests
{
    [Theory]
    [InlineData(true, true, true, HypervisorPlatformState.Ready)]
    [InlineData(true, true, false, HypervisorPlatformState.Ready)]          // a running hypervisor hides the feature
    [InlineData(false, false, true, HypervisorPlatformState.NotEnabled)]
    [InlineData(false, false, false, HypervisorPlatformState.NotEnabled)]   // Hyper-V alone may hide it too
    [InlineData(true, false, true, HypervisorPlatformState.RestartNeeded)]
    [InlineData(true, false, false, HypervisorPlatformState.FirmwareVirtualizationOff)]
    public void What_windows_reports_reads_as_one_state(bool library, bool hypervisor, bool firmware, HypervisorPlatformState expected)
    {
        Assert.Equal(expected, HypervisorPlatform.Classify(library, hypervisor, firmware));
    }

    [Fact]
    public void Automatic_acceleration_needs_both_the_build_and_windows()
    {
        Assert.True(Installation(true, HypervisorPlatformState.Ready).SupportsWhpx);
        Assert.False(Installation(false, HypervisorPlatformState.Ready).SupportsWhpx);
        Assert.False(Installation(true, HypervisorPlatformState.NotEnabled).SupportsWhpx);
        Assert.False(Installation(true, HypervisorPlatformState.RestartNeeded).SupportsWhpx);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]    // done, restart required
    [InlineData(87, false)]
    public void Dism_success_includes_a_pending_restart(int exitCode, bool succeeded)
    {
        Assert.Equal(succeeded, HypervisorPlatform.EnableSucceeded(exitCode));
    }

    private static QemuInstallation Installation(bool build, HypervisorPlatformState platform) => new()
    {
        InstallDirectory = "C:\\qemu",
        SystemBinaries = new Dictionary<Models.GuestArchitecture, string>(),
        QemuImgPath = "qemu-img.exe",
        WhpxInBuild = build,
        Platform = platform
    };
}
