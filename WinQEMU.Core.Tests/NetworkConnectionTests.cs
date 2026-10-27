using WinQEMU.Core.Models;
using WinQEMU.Core.Networking;

namespace WinQEMU.Core.Tests;

/// <summary>A card's connection chosen from one list, and subnets that cannot sit side by side.</summary>
public class NetworkConnectionTests
{
    [Fact]
    public void A_card_plugged_into_a_network_is_one_value_of_the_list()
    {
        var adapter = new NetworkAdapterConfig();
        Assert.Equal(NetworkAdapterConfig.NatConnection, adapter.Connection);

        adapter.Connection = NetworkAdapterConfig.NetworkConnection("abc");

        Assert.Equal(NetworkMode.Network, adapter.Mode);
        Assert.Equal("abc", adapter.NetworkId);
        Assert.Equal("net:abc", adapter.Connection);
    }

    [Fact]
    public void A_list_clearing_its_selection_does_not_unplug_the_card()
    {
        var adapter = new NetworkAdapterConfig { Mode = NetworkMode.Network, NetworkId = "abc" };

        adapter.Connection = null!;
        adapter.Connection = "net:";

        Assert.Equal(NetworkMode.Network, adapter.Mode);
        Assert.Equal("abc", adapter.NetworkId);
    }

    [Fact]
    public void A_card_is_plugged_in_unless_told_otherwise()
    {
        Assert.True(new NetworkAdapterConfig().Connected);
    }

    private static VirtualNetwork Internal(string subnet, int prefixLength = 24, string name = "Interne") =>
        new() { Name = name, Type = VirtualNetworkType.Internal, Subnet = subnet, PrefixLength = prefixLength };

    private static readonly HostSubnet Wifi = new(DhcpScope.Parse("192.168.1.0")!.Value, 24, "la carte « Wi-Fi » de ce PC");

    [Fact]
    public void An_internal_network_on_this_PCs_own_network_is_refused()
    {
        var network = Internal("192.168.1.0");

        Assert.Contains("Wi-Fi", network.ConflictWith(new[] { network }, new[] { Wifi }));
    }

    [Fact]
    public void A_wider_mask_reaching_this_PCs_network_is_refused_too()
    {
        var network = Internal("192.168.0.0", 16);

        Assert.NotNull(network.ConflictWith(new[] { network }, new[] { Wifi }));
    }

    [Fact]
    public void Two_internal_networks_cannot_overlap()
    {
        var first = Internal("10.77.1.0", name: "A");
        var second = Internal("10.77.0.0", 16, "B");

        Assert.Contains("A", second.ConflictWith(new[] { first, second }, Array.Empty<HostSubnet>()));
        Assert.Contains("B", first.ConflictWith(new[] { first, second }, Array.Empty<HostSubnet>()));
    }

    [Fact]
    public void A_private_and_an_internal_network_cannot_share_a_subnet()
    {
        var lan1 = new VirtualNetwork { Name = "LAN 1", Type = VirtualNetworkType.Private, Subnet = "10.77.1.0" };
        var lan2 = Internal("10.77.1.0", name: "LAN 2");

        Assert.Contains("LAN 2", lan1.ConflictWith(new[] { lan1, lan2 }, Array.Empty<HostSubnet>()));
        Assert.Contains("LAN 1", lan2.ConflictWith(new[] { lan1, lan2 }, Array.Empty<HostSubnet>()));
    }

    [Fact]
    public void A_private_network_may_reuse_this_PCs_subnet_since_this_PC_is_not_on_it()
    {
        var network = new VirtualNetwork { Type = VirtualNetworkType.Private, Subnet = "192.168.1.0" };

        Assert.Null(network.ConflictWith(new[] { network }, new[] { Wifi }));
    }

    [Fact]
    public void No_network_may_overlap_QEMUs_NAT()
    {
        var network = new VirtualNetwork { Type = VirtualNetworkType.Private, Subnet = "10.0.2.0" };

        Assert.Contains("NAT", network.ConflictWith(new[] { network }, Array.Empty<HostSubnet>()));
    }

    [Fact]
    public void Separate_subnets_are_accepted()
    {
        var first = Internal("10.77.1.0");
        var second = Internal("10.77.2.0");

        Assert.Null(first.ConflictWith(new[] { first, second }, new[] { Wifi }));
    }

    [Fact]
    public void An_external_network_has_no_subnet_of_its_own_to_conflict()
    {
        var network = new VirtualNetwork { Type = VirtualNetworkType.External, PhysicalAdapterName = "Wi-Fi" };

        Assert.Null(network.Problem);
        Assert.Null(network.ConflictWith(new[] { network }, new[] { Wifi }));
    }
}
