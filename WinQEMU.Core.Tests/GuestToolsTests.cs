using System.Runtime.InteropServices;
using WinQEMU.Core.Input;
using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

// Never beside another class that loads keyboard layouts: one unloading a layout under the other failed it.
[Collection("Keyboard layouts")]
public sealed class GuestToolsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-tools-" + Guid.NewGuid().ToString("N"));

    public GuestToolsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData("0000040C")] // French AZERTY, this PC's
    [InlineData("00000409")] // US
    public void The_typed_command_has_a_key_for_every_character(string layoutId)
    {
        var before = Layouts();
        var layout = LoadKeyboardLayout(layoutId, 0x80);
        try
        {
            Assert.NotEmpty(KeyboardText.ToKeystrokes(GuestTools.LinuxInstallCommand, layout));
        }
        finally
        {
            if (!before.Contains(layout))
                UnloadKeyboardLayout(layout);
        }
    }

    // What the Linux check prints: "present" (spice-vdagent), "headless" (no desktop), "perl".
    [Theory]
    [InlineData("present\nperl\n", GuestToolsState.Complete, null)]
    [InlineData("headless\nperl\n", GuestToolsState.Complete, null)]
    [InlineData("perl\n", GuestToolsState.Incomplete, "spice-vdagent")]
    // A Fedora installed with Xfce had no Perl: nothing could be copied, whatever else was there.
    [InlineData("present\n", GuestToolsState.Incomplete, "Perl")]
    [InlineData("headless\n", GuestToolsState.Incomplete, "Perl")]
    [InlineData("", GuestToolsState.Incomplete, "Perl")]
    public void A_Linux_guest_needs_Perl_and_with_a_desktop_the_clipboard_agent(string output, GuestToolsState state, string? missing)
    {
        var report = GuestTools.Report(output, windowsGuest: false);

        Assert.Equal(state, report.State);
        Assert.Equal(missing, report.Missing);
    }

    [Fact]
    public void A_Windows_guest_is_not_asked_for_Perl()
    {
        Assert.Equal(GuestToolsState.Complete, GuestTools.Report("present", windowsGuest: true).State);
        Assert.Equal(GuestToolsState.Incomplete, GuestTools.Report("", windowsGuest: true).State);
    }

    [Fact]
    public void Perl_is_checked_and_installed_with_the_modules_the_guest_programs_use()
    {
        Assert.Contains("perl -MIO::Handle -MSocket -e 'my $e = $!{EBUSY}'", GuestTools.LinuxCheckScript);
        Assert.Contains("dnf install -y $packages perl-interpreter perl-IO perl-Socket perl-Errno\n", GuestTools.LinuxToolsScript);
        Assert.Contains("apt-get install -y $packages perl-base\n", GuestTools.LinuxToolsScript);
        // The clipboard agent only where there is a desktop to use it.
        Assert.Contains("packages=spice-vdagent", GuestTools.LinuxToolsScript);
        // Each sessions folder on its own: "ls a b" failed when one was missing, and Xfce on X11 read as no desktop.
        Assert.DoesNotContain("ls /usr/share/xsessions", GuestTools.LinuxToolsScript + GuestTools.LinuxCheckScript);
        Assert.Contains("spice-vdagent perl-interpreter perl-IO perl-Socket perl-Errno;", GuestTools.LinuxInstallCommand);
    }

    [Fact]
    public void Tools_are_looked_at_only_in_an_installed_system()
    {
        Assert.True(GuestTools.ShouldCheck(new VmConfig { GuestOs = "ubuntu" }));
        Assert.False(GuestTools.ShouldCheck(new VmConfig { GuestOs = "ubuntu", BootFromIso = true }));
        Assert.False(GuestTools.ShouldCheck(new VmConfig { GuestOs = "windows-11", UnattendedSetup = UnattendedSetupState.Installing }));
        Assert.False(GuestTools.ShouldCheck(new VmConfig { GuestOs = "other" }));
    }

    [Fact]
    public void The_ISO_a_machine_already_uses_is_found()
    {
        var iso = Path.Combine(_folder, "virtio-win-0.1.262.iso");
        File.WriteAllText(iso, "iso");
        var library = new[]
        {
            new VmConfig { IsoPath = Path.Combine(_folder, "ubuntu.iso") },
            new VmConfig { SecondaryIsoPath = iso }
        };

        Assert.Equal(iso, GuestTools.FindVirtioWinIso(null, library, Path.Combine(_folder, "absent")));
    }

    [Fact]
    public void The_one_chosen_before_comes_first_then_the_newest_downloaded()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_folder, "Downloads")).FullName;
        var older = Path.Combine(downloads, "virtio-win-0.1.240.iso");
        var newer = Path.Combine(downloads, "virtio-win-0.1.262.iso");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-30));

        Assert.Equal(newer, GuestTools.FindVirtioWinIso(null, Array.Empty<VmConfig>(), downloads));
        Assert.Equal(older, GuestTools.FindVirtioWinIso(older, Array.Empty<VmConfig>(), downloads));
        Assert.Equal(newer, GuestTools.FindVirtioWinIso(Path.Combine(_folder, "gone.iso"), Array.Empty<VmConfig>(), downloads));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string id, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnloadKeyboardLayout(IntPtr layout);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, IntPtr[]? layouts);

    private static HashSet<IntPtr> Layouts()
    {
        var count = GetKeyboardLayoutList(0, null);
        var layouts = new IntPtr[count];
        GetKeyboardLayoutList(count, layouts);
        return layouts.ToHashSet();
    }
}
