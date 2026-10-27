using System.Xml.Linq;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Windows 11 Setup on a machine without TPM or Secure Boot: the answer file that lifts the hardware
/// check, on a read-only USB stick made from the machine's folder.
/// </summary>
public class WindowsSetupTests
{
    private static VmConfig Windows11()
    {
        var config = Fixtures.Vm("Win11");
        config.GuestOs = "windows-11";
        config.IsoPath = @"C:\iso\Win11_25H2_French_x64.iso";
        config.BootFromIso = true;
        return config;
    }

    [Fact]
    public void A_Windows_11_machine_installing_from_its_ISO_gets_the_setup_stick()
    {
        var config = Windows11();
        var args = Fixtures.Build(config);

        var blockdevs = Fixtures.Values(args, "-blockdev");
        Assert.Contains(blockdevs, b => b.StartsWith("driver=vvfat,", StringComparison.Ordinal)
                                        && b.Contains($"dir={QemuArgumentBuilder.GetWindowsSetupFolder(config)}", StringComparison.Ordinal)
                                        && b.Contains("read-only=on", StringComparison.Ordinal));
        Assert.Contains("usb-storage,id=wq-setup-stick,bus=wq-usb.0,drive=wq-setup,removable=on", Fixtures.Values(args, "-device"));
    }

    [Fact]
    public void The_stick_plugs_into_the_controller_declared_before_it()
    {
        var devices = Fixtures.Values(Fixtures.Build(Windows11()), "-device");

        Assert.True(devices.IndexOf("qemu-xhci,id=wq-usb") < devices.FindIndex(d => d.StartsWith("usb-storage,id=wq-setup-stick", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("windows-10", true, true)]
    [InlineData("windows-11", false, true)]
    [InlineData("windows-11", true, false)]
    public void Other_machines_get_no_stick(string guestOs, bool bootFromIso, bool hasIso)
    {
        var config = Windows11();
        config.GuestOs = guestOs;
        config.BootFromIso = bootFromIso;
        if (!hasIso)
            config.IsoPath = string.Empty;

        Assert.DoesNotContain(Fixtures.Values(Fixtures.Build(config), "-device"), d => d.Contains("wq-setup", StringComparison.Ordinal));
    }

    [Fact]
    public void The_answer_file_only_sets_the_LabConfig_switches_before_the_hardware_check()
    {
        var document = XDocument.Parse(WindowsSetupAnswer.Content);
        XNamespace unattend = "urn:schemas-microsoft-com:unattend";

        var settings = Assert.Single(document.Root!.Elements(unattend + "settings"));
        Assert.Equal("windowsPE", settings.Attribute("pass")!.Value);
        var paths = settings.Descendants(unattend + "Path").Select(p => p.Value).ToList();
        foreach (var check in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck" })
            Assert.Contains($@"reg add HKLM\SYSTEM\Setup\LabConfig /v {check} /t REG_DWORD /d 1 /f", paths);
        Assert.Equal(Enumerable.Range(1, paths.Count).Select(i => i.ToString()),
            settings.Descendants(unattend + "Order").Select(o => o.Value));
        Assert.Empty(document.Descendants(unattend + "UserData"));
    }

    [Fact]
    public void The_answer_file_is_written_once_and_left_alone_after()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wq-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            WindowsSetupAnswer.WriteTo(folder);
            var path = Path.Combine(folder, WindowsSetupAnswer.FileName);
            var written = File.GetLastWriteTimeUtc(path);
            WindowsSetupAnswer.WriteTo(folder);

            Assert.Equal(WindowsSetupAnswer.Content, File.ReadAllText(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
