using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// The clipboard channel: qemu-vdagent for the guest, a password-protected loopback VNC server for the
/// host, since QEMU's SDL display shares no clipboard at all.
/// </summary>
public class ClipboardArgumentTests
{
    private static IReadOnlyList<string> BuildWithClipboard(int port) =>
        QemuArgumentBuilder.Build(Fixtures.Vm(), Fixtures.Installation(), 4444, port);

    [Fact]
    public void Vnc_listens_on_loopback_only_and_requires_a_password()
    {
        Assert.Equal("127.0.0.1:44100,password=on", Fixtures.Value(BuildWithClipboard(50000), "-vnc"));
    }

    [Fact]
    public void Guest_agent_port_has_the_name_spice_vdagent_looks_for()
    {
        var args = BuildWithClipboard(50000);

        Assert.Contains("qemu-vdagent,id=wq-vdagent,name=vdagent,clipboard=on", args);
        Assert.Contains(args, a => a.StartsWith("virtserialport,", StringComparison.Ordinal)
                                   && a.Contains("chardev=wq-vdagent")
                                   && a.Contains("name=com.redhat.spice.0")
                                   // Without an id QEMU never reports the agent connecting.
                                   && a.Contains("id=" + QemuArgumentBuilder.ClipboardAgentPortId));
    }

    [Fact]
    public void No_port_means_no_clipboard_devices()
    {
        var args = Fixtures.Build(Fixtures.Vm());

        Assert.DoesNotContain("-vnc", args);
        Assert.DoesNotContain(args, a => a.Contains("vdagent", StringComparison.Ordinal));
    }

    [Fact]
    public void A_port_below_the_vnc_range_is_ignored_rather_than_misnumbered()
    {
        // QEMU only takes a display number, i.e. port - 5900; a lower port cannot be expressed.
        Assert.DoesNotContain("-vnc", BuildWithClipboard(5000));
    }
}
