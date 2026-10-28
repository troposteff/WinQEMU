using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>Ubuntu's unattended installation: its answers, and the first start that boots the installer itself.</summary>
public class UbuntuUnattendedSetupTests
{
    private static UnattendedLinux Settings() =>
        new("stephane", Sha512Crypt.Hash("secret", "abcdefgh"), "poste-dev", "fr_FR.UTF-8", "fr", "Europe/Paris");

    [Fact]
    public void The_answers_hold_the_hash_never_the_password()
    {
        var userData = UbuntuUnattendedSetup.UserData(Settings());

        Assert.StartsWith("#cloud-config", userData);
        Assert.Contains("autoinstall:", userData);
        Assert.Contains($"password: \"{Sha512Crypt.Hash("secret", "abcdefgh")}\"", userData);
        Assert.DoesNotContain("secret\"", userData);
        Assert.Contains("username: \"stephane\"", userData);
        Assert.Contains("hostname: \"poste-dev\"", userData);
        Assert.Contains("timezone: \"Europe/Paris\"", userData);
        Assert.Contains("layout: \"fr\"", userData);
    }

    [Fact]
    public void The_guest_agent_and_the_clipboard_agent_come_with_the_installation()
    {
        var userData = UbuntuUnattendedSetup.UserData(Settings());

        Assert.Contains("- qemu-guest-agent", userData);
        Assert.Contains("- spice-vdagent", userData);
    }

    [Theory]
    [InlineData("Poste de développement", "poste-de-developpement")]
    [InlineData("Ubuntu 26.04", "ubuntu-26-04")]
    [InlineData("***", "ubuntu")]
    public void A_host_name_is_made_from_the_machines_name(string machine, string expected)
    {
        Assert.Equal(expected, UbuntuUnattendedSetup.HostnameFor(machine));
    }

    [Theory]
    [InlineData("stephane", true)]
    [InlineData("Stephane", false)]
    [InlineData("2cool", false)]
    [InlineData("root", false)]
    [InlineData("jean-pierre_2", true)]
    public void User_names_follow_Linux_rules(string name, bool accepted)
    {
        Assert.Equal(accepted, UbuntuUnattendedSetup.WhyUserNameIsRefused(name) is null);
    }

    private static VmConfig Ubuntu(UnattendedSetupState state)
    {
        var config = Fixtures.Vm("Ubuntu");
        config.GuestOs = "ubuntu";
        config.IsoPath = @"C:\iso\ubuntu-26.04-desktop-amd64.iso";
        config.BootFromIso = false;
        config.UnattendedSetup = state;
        return config;
    }

    [Fact]
    public void The_first_start_boots_the_installer_with_autoinstall_and_its_answers()
    {
        var config = Ubuntu(UnattendedSetupState.Pending);
        var args = Fixtures.Build(config);
        var folder = QemuArgumentBuilder.GetWindowsSetupFolder(config);

        Assert.Equal(Path.Combine(folder, "vmlinuz"), Fixtures.Value(args, "-kernel"));
        Assert.Equal(Path.Combine(folder, "initrd"), Fixtures.Value(args, "-initrd"));
        Assert.StartsWith("autoinstall ---", Fixtures.Value(args, "-append"));
        Assert.Contains(Fixtures.Values(args, "-blockdev"), b => b.Contains("label=CIDATA", StringComparison.Ordinal));
    }

    [Fact]
    public void Boot_index_0_is_left_to_the_kernel_QEMU_boots()
    {
        // A device given bootindex 0 next to -kernel stops QEMU: "The bootindex 0 has already been used".
        var devices = Fixtures.Values(Fixtures.Build(Ubuntu(UnattendedSetupState.Pending)), "-device");

        Assert.DoesNotContain(devices, d => d.Contains("bootindex=0", StringComparison.Ordinal));
        Assert.Contains(devices, d => d.Contains("bootindex=1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UnattendedSetupState.Installing)]
    [InlineData(UnattendedSetupState.None)]
    public void Later_starts_are_the_disks(UnattendedSetupState state)
    {
        var args = Fixtures.Build(Ubuntu(state));

        Assert.Null(Fixtures.Value(args, "-kernel"));
        Assert.DoesNotContain(Fixtures.Values(args, "-blockdev"), b => b.Contains("CIDATA", StringComparison.Ordinal));
    }
}
