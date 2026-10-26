using WinQEMU.Core.Models;

namespace WinQEMU.Core.Tests;

public class NetworkTests
{
    private static NetworkAdapterConfig Adapter(NetworkMode mode) =>
        new() { Id = "net0", Mode = mode, Model = NicModel.VirtioNet };

    [Fact]
    public void No_adapter_means_no_network_rather_than_a_default_nic()
    {
        // Left alone, QEMU attaches a user-mode NIC of its own choosing.
        Assert.Equal("none", Fixtures.Value(Fixtures.Build(Fixtures.Vm()), "-nic"));
    }

    [Fact]
    public void An_adapter_set_to_none_is_the_same_as_having_none()
    {
        var vm = Fixtures.Vm();
        vm.NetworkAdapters.Add(Adapter(NetworkMode.None));

        Assert.Equal("none", Fixtures.Value(Fixtures.Build(vm), "-nic"));
    }

    [Fact]
    public void Nat_uses_user_mode_networking()
    {
        var vm = Fixtures.Vm();
        vm.NetworkAdapters.Add(Adapter(NetworkMode.Nat));

        var args = Fixtures.Build(vm);

        Assert.Equal("user,id=net0", Fixtures.Value(args, "-netdev"));
        Assert.Equal("virtio-net-pci,netdev=net0", Fixtures.Device(args, "virtio-net-pci"));
    }

    [Fact]
    public void Port_forwards_are_appended_to_the_nat_backend()
    {
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Nat);
        adapter.PortForwards.Add(new PortForward
        {
            Protocol = PortForwardProtocol.Tcp, HostAddress = "127.0.0.1", HostPort = 2222, GuestPort = 22
        });
        adapter.PortForwards.Add(new PortForward
        {
            Protocol = PortForwardProtocol.Udp, HostAddress = "0.0.0.0", HostPort = 5353, GuestPort = 53
        });
        vm.NetworkAdapters.Add(adapter);

        var netdev = Fixtures.Value(Fixtures.Build(vm), "-netdev")!;

        Assert.Contains("hostfwd=tcp:127.0.0.1:2222-:22", netdev);
        Assert.Contains("hostfwd=udp:0.0.0.0:5353-:53", netdev);
    }

    [Fact]
    public void A_forward_without_a_host_address_stays_on_the_loopback()
    {
        // Defaulting to the loopback keeps a guest service off the LAN unless that was asked for.
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Nat);
        adapter.PortForwards.Add(new PortForward { HostAddress = "", HostPort = 8080, GuestPort = 80 });
        vm.NetworkAdapters.Add(adapter);

        Assert.Contains("hostfwd=tcp:127.0.0.1:8080-:80", Fixtures.Value(Fixtures.Build(vm), "-netdev")!);
    }

    [Fact]
    public void A_bridged_adapter_names_the_host_tap_interface()
    {
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Bridged);
        adapter.BridgeInterface = "tap0";
        vm.NetworkAdapters.Add(adapter);

        Assert.Equal("tap,id=net0,ifname=tap0", Fixtures.Value(Fixtures.Build(vm), "-netdev"));
    }

    [Fact]
    public void An_internal_segment_becomes_a_multicast_socket()
    {
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Internal);
        adapter.InternalSegment = 7;
        vm.NetworkAdapters.Add(adapter);

        Assert.Equal("socket,id=net0,mcast=230.0.0.7:1234", Fixtures.Value(Fixtures.Build(vm), "-netdev"));
    }

    [Fact]
    public void An_out_of_range_segment_is_clamped_instead_of_producing_a_bad_address()
    {
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Internal);
        adapter.InternalSegment = 999;
        vm.NetworkAdapters.Add(adapter);

        Assert.Equal("socket,id=net0,mcast=230.0.0.254:1234", Fixtures.Value(Fixtures.Build(vm), "-netdev"));
    }

    [Fact]
    public void A_fixed_mac_is_passed_to_the_device()
    {
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Nat);
        adapter.MacAddress = "52:54:00:ab:cd:ef";
        vm.NetworkAdapters.Add(adapter);

        Assert.Contains("mac=52:54:00:ab:cd:ef", Fixtures.Device(Fixtures.Build(vm), "virtio-net-pci")!);
    }

    [Theory]
    [InlineData(NicModel.VirtioNet, "virtio-net-pci")]
    [InlineData(NicModel.E1000, "e1000")]
    [InlineData(NicModel.E1000e, "e1000e")]
    [InlineData(NicModel.Rtl8139, "rtl8139")]
    public void Each_nic_model_maps_to_its_qemu_device(NicModel model, string expected)
    {
        var vm = Fixtures.Vm();
        var adapter = Adapter(NetworkMode.Nat);
        adapter.Model = model;
        vm.NetworkAdapters.Add(adapter);

        Assert.NotNull(Fixtures.Device(Fixtures.Build(vm), expected));
    }

    [Fact]
    public void Two_adapters_each_get_their_own_backend()
    {
        var vm = Fixtures.Vm();
        vm.NetworkAdapters.Add(Adapter(NetworkMode.Nat));
        var second = Adapter(NetworkMode.Internal);
        second.Id = "net1";
        vm.NetworkAdapters.Add(second);

        var netdevs = Fixtures.Values(Fixtures.Build(vm), "-netdev");

        Assert.Equal(2, netdevs.Count);
        Assert.Contains("id=net0", netdevs[0]);
        Assert.Contains("id=net1", netdevs[1]);
    }
}
