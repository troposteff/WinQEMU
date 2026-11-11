using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;
using Xunit;

namespace WinQEMU.Core.Tests;

public class LinuxUnattendedSetupTests
{
    private static UnattendedLinux Settings(LinuxDesktop desktop = LinuxDesktop.Gnome) =>
        new("stephane", "$6$salt$hash", "debian-test", "fr_FR.UTF-8", "fr", "Europe/Paris", desktop);

    [Fact]
    public void The_preseed_is_appended_to_the_initrd_as_a_second_archive()
    {
        var original = Encoding.ASCII.GetBytes("ORIGINAL-INITRD");

        var initrd = LinuxUnattendedSetup.WithFile(original, "preseed.cfg", "d-i a b\r\n");

        Assert.Equal(original, initrd[..original.Length]);
        using var gzip = new GZipStream(new MemoryStream(initrd[original.Length..]), CompressionMode.Decompress);
        using var cpio = new MemoryStream();
        gzip.CopyTo(cpio);
        var text = Encoding.ASCII.GetString(cpio.ToArray());
        // newc: magic, then the name and the data, each padded to four bytes; LF only for the installer.
        Assert.StartsWith("070701", text);
        Assert.Contains("preseed.cfg\0", text);
        Assert.Contains("d-i a b\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.Contains("TRAILER!!!", text);
        Assert.Equal(0, cpio.Length % 4);
    }

    [Fact]
    public void Debian_creates_the_account_and_chooses_its_tasks()
    {
        var desktop = LinuxUnattendedSetup.DebianPreseed(Settings());
        var console = LinuxUnattendedSetup.DebianPreseed(Settings(LinuxDesktop.None));

        Assert.Contains("d-i passwd/user-password-crypted password $6$salt$hash", desktop);
        Assert.Contains("d-i debian-installer/locale string fr_FR.UTF-8", desktop);
        Assert.Contains("gnome-desktop", desktop);
        Assert.Contains("ssh-server", console);
        Assert.DoesNotContain("gnome-desktop", console);
        Assert.Contains("pkgsel/include string qemu-guest-agent spice-vdagent", console);
    }

    [Fact]
    public void Fedora_kickstart_has_a_wheel_user_and_the_environment()
    {
        var kickstart = LinuxUnattendedSetup.FedoraKickstart(Settings());

        Assert.Contains("user --name=stephane --gecos=\"stephane\" --groups=wheel --iscrypted --password=$6$salt$hash", kickstart);
        Assert.Contains("@^workstation-product-environment", kickstart);
        var console = LinuxUnattendedSetup.FedoraKickstart(Settings(LinuxDesktop.None));
        Assert.Contains("@^server-product-environment", console);
        Assert.Contains("reboot", kickstart);
        // Re-activating the card cut it off as Anaconda set up its sources: "Could not resolve host".
        Assert.Contains("\nnetwork --bootproto=dhcp --hostname=", kickstart.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("--activate", kickstart);
        // Anaconda waits for the network and DNS before setting up its sources: its first lookup failed otherwise.
        Assert.Contains("\n%pre --log=/tmp/winqemu-pre.log\nnm-online", kickstart.ReplaceLineEndings("\n"));
        Assert.Contains("https://mirrors.fedoraproject.org", kickstart);
        Assert.Equal("inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-45 inst.ks=hd:LABEL=OEMDRV:/ks.cfg quiet",
            LinuxUnattendedSetup.FedoraCommandLine("inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-45"));
        // A text-mode installation otherwise leaves Workstation on a text login.
        Assert.Contains("\nxconfig --startxonboot\n", kickstart.ReplaceLineEndings("\n"));
        Assert.Contains("\nskipx\n", console.ReplaceLineEndings("\n"));
        // KDE's own first-boot assistant would otherwise ask to set up an account that exists.
        Assert.Contains("\n-plasma-setup\n%end", LinuxUnattendedSetup.FedoraKickstart(Settings(LinuxDesktop.Kde)).ReplaceLineEndings("\n"));
        // Perl, which WinQEMU's guest programs are written in: Fedora with Xfce came without it.
        Assert.Contains("\nspice-vdagent\nperl-interpreter\nperl-IO\nperl-Socket\nperl-Errno\n%end", kickstart.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("plasma-setup", kickstart);
        // SELinux confines the agent: what it ran could not reach /home.
        Assert.Contains("%post\nif command -v semodule", kickstart.ReplaceLineEndings("\n"));
        Assert.Contains("(typepermissive virt_qemu_ga_t)", kickstart);
    }

    [Fact]
    public void AutoYast_is_well_formed_and_installs_the_agents()
    {
        var profile = XDocument.Parse(LinuxUnattendedSetup.AutoYast(Settings(LinuxDesktop.Kde)));
        XNamespace yast = "http://www.suse.com/1.0/yast2ns";

        Assert.Contains(profile.Descendants(yast + "pattern"), p => p.Value == "kde_plasma");
        Assert.Contains(profile.Descendants(yast + "package"), p => p.Value == "qemu-guest-agent");
        Assert.Equal("french", profile.Descendants(yast + "keymap").Single().Value);
        Assert.Equal(2, profile.Descendants(yast + "user").Count());
        Assert.Equal("openSUSE", profile.Descendants(yast + "product").Single().Value);
        // The OEMDRV stick is the first disk: never the one to install on.
        Assert.Equal("size_k", profile.Descendants(yast + "skip_key").Single().Value);
        Assert.Equal("false", profile.Descendants(yast + "add_crash_kernel").Single().Value);
        // The graphical layout, which <keymap> leaves at "us" on the login screen.
        var keyboard = profile.Descendants(yast + "file").Single();
        Assert.Equal("/etc/X11/xorg.conf.d/00-keyboard.conf", keyboard.Element(yast + "file_path")!.Value);
        Assert.Contains("Option \"XkbLayout\" \"fr\"", keyboard.Element(yast + "file_contents")!.Value);
        Assert.StartsWith("Section \"InputClass\"", keyboard.Element(yast + "file_contents")!.Value);
        // The agent may run commands, out of SELinux's confinement.
        var script = profile.Descendants(yast + "chroot-scripts").Single().Descendants(yast + "source").Single().Value;
        Assert.Contains("(typepermissive virt_qemu_ga_t)", script);
        Assert.Contains("ExecStart=/usr/bin/qemu-ga -p /dev/virtio-ports/org.qemu.guest_agent.0\\n", script);
        Assert.DoesNotContain("block-rpcs", script);
    }

    [Fact]
    public void A_Tumbleweed_DVD_installs_with_the_online_repository_too()
    {
        XNamespace yast = "http://www.suse.com/1.0/yast2ns";
        var dvd = XDocument.Parse(LinuxUnattendedSetup.AutoYast(Settings(LinuxDesktop.Kde), "openSUSE",
            LinuxUnattendedSetup.TumbleweedOnlineRepository));
        var other = XDocument.Parse(LinuxUnattendedSetup.AutoYast(Settings(LinuxDesktop.Kde)));

        // spice-vdagent is not on the DVD: AutoYaST stopped on "package not available".
        Assert.Equal("https://download.opensuse.org/tumbleweed/repo/oss/", dvd.Descendants(yast + "media_url").Single().Value);
        Assert.Empty(other.Descendants(yast + "add-on"));
        Assert.True(LinuxUnattendedSetup.IsTumbleweedDvd(
            "openSUSE - openSUSE-Tumbleweed-DVD-x86_64-Build4888.1-Media\nopenSUSE-Tumbleweed-DVD-x86_64-Build4888.1\n1\n"));
        Assert.False(LinuxUnattendedSetup.IsTumbleweedDvd("openSUSE - openSUSE-Tumbleweed-NET-x86_64-Build4888.1-Media\n"));
        Assert.False(LinuxUnattendedSetup.IsTumbleweedDvd("openSUSE - openSUSE-Leap-16.0-DVD-x86_64-Build1.1-Media\n"));
    }

    // The names each installer knows the environment by: tasksel's task, the comps environment, the pattern.
    [Theory]
    [InlineData(LinuxDesktop.Gnome, "gnome-desktop", "@^workstation-product-environment", "gnome")]
    [InlineData(LinuxDesktop.Kde, "kde-desktop", "@^kde-desktop-environment", "kde_plasma")]
    [InlineData(LinuxDesktop.Xfce, "xfce-desktop", "@^xfce-desktop-environment", "xfce")]
    [InlineData(LinuxDesktop.Cinnamon, "cinnamon-desktop", "@^cinnamon-desktop-environment", "cinnamon")]
    [InlineData(LinuxDesktop.Mate, "mate-desktop", "@^mate-desktop-environment", "mate")]
    [InlineData(LinuxDesktop.Lxqt, "lxqt-desktop", "@^lxqt-desktop-environment", "lxqt")]
    public void Each_desktop_is_named_as_each_installer_knows_it(LinuxDesktop desktop, string task, string environment, string pattern)
    {
        var settings = Settings(desktop);
        var profile = XDocument.Parse(LinuxUnattendedSetup.AutoYast(settings));
        XNamespace yast = "http://www.suse.com/1.0/yast2ns";

        Assert.Contains($"standard, desktop, {task}", LinuxUnattendedSetup.DebianPreseed(settings));
        Assert.Contains($"\n{environment}\n", LinuxUnattendedSetup.FedoraKickstart(settings).ReplaceLineEndings("\n"));
        var patterns = profile.Descendants(yast + "pattern").Select(p => p.Value).ToList();
        Assert.Contains(pattern, patterns);
        Assert.Contains("x11", patterns);
        Assert.Equal("graphical", profile.Descendants(yast + "default_target").Single().Value);
        Assert.Contains(desktop, LinuxUnattendedSetup.Desktops);
        Assert.NotNull(LinuxUnattendedSetup.DesktopName(desktop));
    }

    [Fact]
    public void No_desktop_is_a_console_with_ssh()
    {
        var profile = XDocument.Parse(LinuxUnattendedSetup.AutoYast(Settings(LinuxDesktop.None)));
        XNamespace yast = "http://www.suse.com/1.0/yast2ns";

        Assert.DoesNotContain(profile.Descendants(yast + "pattern"), p => p.Value == "x11");
        Assert.Empty(profile.Descendants(yast + "file"));
        Assert.Equal("multi-user", profile.Descendants(yast + "default_target").Single().Value);
        Assert.Equal(LinuxDesktop.Kde, LinuxUnattendedSetup.DefaultDesktop("opensuse"));
        Assert.Equal(LinuxDesktop.Gnome, LinuxUnattendedSetup.DefaultDesktop("debian"));
    }

    private static VmConfig Pending(string guestOs, string? commandLine = null)
    {
        var config = Fixtures.Vm(guestOs);
        config.GuestOs = guestOs;
        config.FolderPath = Path.Combine(Path.GetTempPath(), "wq-linux-" + Guid.NewGuid().ToString("N"));
        config.IsoPath = @"C:\iso\installer.iso";
        config.BootFromIso = false;
        config.UnattendedSetup = UnattendedSetupState.Pending;
        if (commandLine is not null)
        {
            var folder = QemuArgumentBuilder.GetWindowsSetupFolder(config);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, LinuxUnattendedSetup.CommandLineFile), commandLine);
        }
        return config;
    }

    [Fact]
    public void Fedora_and_openSUSE_read_their_answers_from_an_OEMDRV_stick()
    {
        var config = Pending("fedora", "inst.stage2=hd:LABEL=X inst.ks=hd:LABEL=OEMDRV:/ks.cfg quiet");
        try
        {
            var args = Fixtures.Build(config);

            Assert.Equal("inst.stage2=hd:LABEL=X inst.ks=hd:LABEL=OEMDRV:/ks.cfg quiet", Fixtures.Value(args, "-append"));
            Assert.Contains(Fixtures.Values(args, "-blockdev"), b => b.Contains("label=OEMDRV", StringComparison.Ordinal));
            Assert.Contains(Fixtures.Values(args, "-blockdev"), b => b.Contains(@"winqemu-setup\oemdrv", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(config.FolderPath, recursive: true);
        }
    }

    [Fact]
    public void Debian_needs_no_stick()
    {
        var config = Pending("debian", "priority=critical --- quiet");
        try
        {
            var args = Fixtures.Build(config);

            Assert.Equal("priority=critical --- quiet", Fixtures.Value(args, "-append"));
            Assert.DoesNotContain(Fixtures.Values(args, "-blockdev"), b => b.Contains("wq-cidata", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(config.FolderPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("ubuntu", true)]
    [InlineData("debian", true)]
    [InlineData("fedora", true)]
    [InlineData("opensuse", true)]
    [InlineData("arch", false)]
    public void Supported_systems(string guestOs, bool supported) =>
        Assert.Equal(supported, LinuxUnattendedSetup.Supports(guestOs));
}
