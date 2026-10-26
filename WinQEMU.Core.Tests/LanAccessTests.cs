using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>A NAT guest made reachable from the local network through host forwards.</summary>
public class LanAccessTests
{
    private static bool AllFree(int port) => true;

    [Fact]
    public void A_privileged_guest_port_moves_10000_up_on_the_host()
    {
        // Below 1024 belongs to Windows' own services (SSH server, IIS) even when nothing listens yet.
        Assert.Equal(10022, LanAccess.ChooseHostPort(22, Array.Empty<int>(), AllFree));
        Assert.Equal(10080, LanAccess.ChooseHostPort(80, Array.Empty<int>(), AllFree));
    }

    [Fact]
    public void Remote_desktop_moves_up_only_when_this_pc_already_listens_on_it()
    {
        Assert.Equal(3389, LanAccess.ChooseHostPort(3389, Array.Empty<int>(), AllFree));
        Assert.Equal(13389, LanAccess.ChooseHostPort(3389, Array.Empty<int>(), port => port != 3389));
    }

    [Fact]
    public void An_unprivileged_guest_port_keeps_its_number_when_free()
    {
        Assert.Equal(8080, LanAccess.ChooseHostPort(8080, Array.Empty<int>(), AllFree));
    }

    [Fact]
    public void Ports_taken_by_other_forwards_or_in_use_are_skipped()
    {
        // Another machine already forwards 10022; something listens on 20000.
        Assert.Equal(20001, LanAccess.ChooseHostPort(22, new[] { 10022 }, port => port != 20000));
        Assert.Equal(18080, LanAccess.ChooseHostPort(8080, new[] { 8080 }, AllFree));
    }

    [Theory]
    [InlineData(22, 10022, "ssh -p 10022 utilisateur@192.168.1.170")]
    [InlineData(3389, 13389, "mstsc /v:192.168.1.170:13389")]
    [InlineData(80, 10080, "http://192.168.1.170:10080")]
    [InlineData(443, 443, "https://192.168.1.170")]
    [InlineData(5432, 5432, "192.168.1.170:5432")]
    public void Each_service_says_how_to_reach_it(int guestPort, int hostPort, string expected)
    {
        var forward = new PortForward { GuestPort = guestPort, HostPort = hostPort, HostAddress = LanAccess.AllInterfaces };

        Assert.Equal(expected, LanAccess.UsageOf(forward, "192.168.1.170"));
    }

    [Fact]
    public void The_rules_are_what_hostfwd_add_and_hostfwd_remove_take()
    {
        var forward = new PortForward { Protocol = PortForwardProtocol.Udp, GuestPort = 53, HostPort = 10053, HostAddress = LanAccess.AllInterfaces };

        Assert.Equal("udp:0.0.0.0:10053-:53", LanAccess.RuleOf(forward));
        Assert.Equal("udp:0.0.0.0:10053", LanAccess.RemovalRuleOf(forward));
    }

    [Fact]
    public void Only_a_forward_on_every_interface_is_reachable_from_the_network()
    {
        Assert.True(LanAccess.IsOnNetwork(new PortForward { HostAddress = "0.0.0.0" }));
        Assert.False(LanAccess.IsOnNetwork(new PortForward()));   // 127.0.0.1 by default
    }

    [Fact]
    public void A_known_service_is_named_and_any_other_port_described()
    {
        Assert.Equal("Bureau à distance", LanAccess.LabelOf(new PortForward { GuestPort = 3389 }));
        Assert.Equal("Port 5432", LanAccess.LabelOf(new PortForward { GuestPort = 5432 }));
        Assert.Equal("Port 53 (UDP)", LanAccess.LabelOf(new PortForward { GuestPort = 53, Protocol = PortForwardProtocol.Udp }));
    }
}
