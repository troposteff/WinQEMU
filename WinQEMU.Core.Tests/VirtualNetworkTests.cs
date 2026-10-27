using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinQEMU.Core.Models;
using WinQEMU.Core.Networking;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Virtual networks served by WinQEMU's own switch: the frames QEMU's stream netdev exchanges with it,
/// and the DHCP server inside it.
/// </summary>
public class VirtualNetworkTests
{
    private static readonly byte[] Prefix = { 10, 77, 1 };
    private static readonly byte[] MacA = { 0x52, 0x54, 0x00, 0x00, 0x00, 0x0A };
    private static readonly byte[] MacB = { 0x52, 0x54, 0x00, 0x00, 0x00, 0x0B };
    private static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    private static byte[] EthernetFrame(byte[] destination, byte[] source, ushort type, byte[] payload)
    {
        var frame = new byte[14 + payload.Length];
        destination.CopyTo(frame, 0);
        source.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), type);
        payload.CopyTo(frame, 14);
        return frame;
    }

    /// <summary>A DHCP client message from <paramref name="mac"/>, inside UDP 68 → 67 broadcast.</summary>
    private static byte[] DhcpFrame(byte[] mac, byte type, byte[]? requested = null, byte[]? serverId = null)
    {
        var options = new List<byte> { 53, 1, type };
        if (requested is not null) { options.AddRange(new byte[] { 50, 4 }); options.AddRange(requested); }
        if (serverId is not null) { options.AddRange(new byte[] { 54, 4 }); options.AddRange(serverId); }
        options.Add(255);

        var bootp = new byte[240 + options.Count];
        bootp[0] = 1; bootp[1] = 1; bootp[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(bootp.AsSpan(4), 0x12345678);
        mac.CopyTo(bootp, 28);
        BinaryPrimitives.WriteUInt32BigEndian(bootp.AsSpan(236), 0x63825363);
        options.CopyTo(bootp, 240);

        var ip = new byte[20 + 8 + bootp.Length];
        ip[0] = 0x45; ip[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        ip[16] = ip[17] = ip[18] = ip[19] = 255;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(20), 68);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), 67);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(24), (ushort)(8 + bootp.Length));
        bootp.CopyTo(ip, 28);
        return EthernetFrame(Broadcast, mac, 0x0800, ip);
    }

    private static (byte Type, byte[] YourAddress, byte[]? Mask) ParseReply(byte[] frame)
    {
        var bootp = frame.AsSpan(14 + 20 + 8);
        byte type = 0;
        byte[]? mask = null;
        for (var i = 240; bootp[i] != 255; i += 2 + bootp[i + 1])
        {
            if (bootp[i] == 53) type = bootp[i + 2];
            if (bootp[i] == 1) mask = bootp.Slice(i + 2, 4).ToArray();
        }
        return (type, bootp.Slice(16, 4).ToArray(), mask);
    }

    [Fact]
    public void A_discover_is_offered_an_address_of_the_network_and_a_request_acknowledged()
    {
        var dhcp = new DhcpResponder(Prefix);

        var offer = ParseReply(dhcp.Answer(DhcpFrame(MacA, 1))!);
        Assert.Equal(2, offer.Type);
        Assert.Equal(Prefix, offer.YourAddress[..3]);
        Assert.InRange(offer.YourAddress[3], 100, 199);
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, offer.Mask);

        var ack = ParseReply(dhcp.Answer(DhcpFrame(MacA, 3, offer.YourAddress, new byte[] { 10, 77, 1, 254 }))!);
        Assert.Equal(5, ack.Type);
        Assert.Equal(offer.YourAddress, ack.YourAddress);
    }

    [Fact]
    public void Two_cards_get_two_addresses_and_each_keeps_its_own()
    {
        var dhcp = new DhcpResponder(Prefix);

        var a = ParseReply(dhcp.Answer(DhcpFrame(MacA, 1))!).YourAddress;
        var b = ParseReply(dhcp.Answer(DhcpFrame(MacB, 1))!).YourAddress;

        Assert.NotEqual(a, b);
        Assert.Equal(a, ParseReply(dhcp.Answer(DhcpFrame(MacA, 1))!).YourAddress);
    }

    [Fact]
    public void A_request_for_another_address_is_refused_so_the_guest_starts_over()
    {
        var dhcp = new DhcpResponder(Prefix);

        // A guest coming from another network asks to keep the address it had there.
        Assert.Equal(6, ParseReply(dhcp.Answer(DhcpFrame(MacA, 3, new byte[] { 192, 168, 1, 50 }))!).Type);
    }

    [Fact]
    public void A_request_to_another_server_gets_no_answer()
    {
        var dhcp = new DhcpResponder(Prefix);

        Assert.Null(dhcp.Answer(DhcpFrame(MacA, 3, new byte[] { 10, 77, 1, 120 }, new byte[] { 10, 77, 1, 1 })));
    }

    [Fact]
    public void The_server_answers_arp_for_its_own_address_only()
    {
        var dhcp = new DhcpResponder(Prefix);
        byte[] Arp(byte last)
        {
            var payload = new byte[28];
            BinaryPrimitives.WriteUInt16BigEndian(payload, 1);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), 0x0800);
            payload[4] = 6; payload[5] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), 1);
            MacA.CopyTo(payload, 8);
            new byte[] { 10, 77, 1, 120 }.CopyTo(payload, 14);
            new byte[] { 10, 77, 1, last }.CopyTo(payload, 24);
            return EthernetFrame(Broadcast, MacA, 0x0806, payload);
        }

        var reply = dhcp.Answer(Arp(254))!;
        Assert.Equal(MacA, reply[..6]);
        Assert.Equal(2, reply[21]);
        Assert.Equal(DhcpResponder.Mac, reply[22..28]);
        Assert.Null(dhcp.Answer(Arp(1)));
    }

    // ---- The switch, over real loopback connections as QEMU makes them ----

    private static async Task<NetworkStream> ConnectAsync(VirtualSwitch network)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, network.Port);
        return client.GetStream();
    }

    private static async Task SendAsync(Stream stream, byte[] frame)
    {
        var message = new byte[4 + frame.Length];
        BinaryPrimitives.WriteInt32BigEndian(message, frame.Length);
        frame.CopyTo(message, 4);
        await stream.WriteAsync(message);
    }

    private static async Task<byte[]?> ReceiveAsync(Stream stream, int milliseconds = 2000)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            var frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(frame, timeout.Token);
            return frame;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task WaitForPortsAsync(VirtualSwitch network, int count)
    {
        for (var i = 0; i < 50 && network.ConnectedPorts < count; i++)
            await Task.Delay(20);
    }

    [Fact]
    public async Task A_broadcast_reaches_the_others_and_a_reply_goes_back_to_the_sender_alone()
    {
        await using var network = new VirtualSwitch(dhcp: null);
        network.Start();
        var a = await ConnectAsync(network);
        var b = await ConnectAsync(network);
        var c = await ConnectAsync(network);
        await WaitForPortsAsync(network, 3);

        var hello = EthernetFrame(Broadcast, MacA, 0x88B5, new byte[46]);
        await SendAsync(a, hello);
        Assert.Equal(hello, await ReceiveAsync(b));
        Assert.Equal(hello, await ReceiveAsync(c));

        // The switch learned where MacA is: B's answer goes to A, not to C.
        var answer = EthernetFrame(MacA, MacB, 0x88B5, new byte[46]);
        await SendAsync(b, answer);
        Assert.Equal(answer, await ReceiveAsync(a));
        Assert.Null(await ReceiveAsync(c, 300));
    }

    [Fact]
    public async Task A_guest_on_the_switch_gets_its_address_from_the_switch()
    {
        await using var network = new VirtualSwitch(new DhcpResponder(Prefix));
        network.Start();
        var a = await ConnectAsync(network);
        await WaitForPortsAsync(network, 1);

        await SendAsync(a, DhcpFrame(MacA, 1));
        var offer = await ReceiveAsync(a);

        Assert.NotNull(offer);
        Assert.Equal(2, ParseReply(offer!).Type);
        Assert.Equal(ParseReply(offer!).YourAddress, network.Dhcp!.LeaseOf(new PhysicalAddress(MacA)));
    }

    [Fact]
    public void A_card_on_a_virtual_network_connects_to_its_switch_and_reconnects()
    {
        var config = Fixtures.Vm();
        config.NetworkAdapters.Add(new NetworkAdapterConfig { Id = "net1", Mode = NetworkMode.Network, NetworkId = "lab" });

        var args = QemuArgumentBuilder.Build(config, Fixtures.Installation(), 4444, networkPorts: id => id == "lab" ? 50123 : null);

        Assert.Contains("stream,id=net1,server=off,addr.type=inet,addr.host=127.0.0.1,addr.port=50123,reconnect-ms=1000",
            Fixtures.Values(args, "-netdev"));
    }

    [Fact]
    public void A_card_on_a_deleted_network_cannot_start()
    {
        var config = Fixtures.Vm();
        config.NetworkAdapters.Add(new NetworkAdapterConfig { Id = "net1", Mode = NetworkMode.Network, NetworkId = "gone" });

        var error = Assert.Throws<InvalidOperationException>(() =>
            QemuArgumentBuilder.Build(config, Fixtures.Installation(), 4444, networkPorts: _ => null));
        Assert.Contains("aucun réseau virtuel existant", error.Message);
    }

    [Fact]
    public void This_pcs_adapter_is_given_the_first_address_of_an_internal_network()
    {
        var hostMac = new PhysicalAddress(new byte[] { 0x00, 0xFF, 0x12, 0x34, 0x56, 0x78 });
        var dhcp = new DhcpResponder(Prefix, new Dictionary<PhysicalAddress, byte> { [hostMac] = 1 });

        Assert.Equal(new byte[] { 10, 77, 1, 1 }, ParseReply(dhcp.Answer(DhcpFrame(hostMac.GetAddressBytes(), 1))!).YourAddress);
        // A machine still gets one from the pool, never .1.
        Assert.InRange(ParseReply(dhcp.Answer(DhcpFrame(MacA, 1))!).YourAddress[3], 100, 199);
    }

    [Fact]
    public void An_internal_network_says_where_this_pc_is_on_it()
    {
        var network = new VirtualNetwork
        {
            Type = VirtualNetworkType.Internal,
            Subnet = "10.77.4.0",
            TapAdapterName = "Ethernet 3"
        };

        Assert.Equal("10.77.4.1", network.HostAddress);
        Assert.Contains("ce PC en 10.77.4.1 sur « Ethernet 3 »", network.SubnetText);
    }

    [Fact]
    public void Each_new_network_gets_a_subnet_of_its_own()
    {
        var existing = new[] { new VirtualNetwork { Subnet = "10.77.1.0" }, new VirtualNetwork { Subnet = "10.77.2.0" } };

        Assert.Equal("10.77.3.0", VirtualNetwork.FreeSubnet(existing));
        Assert.Equal(new byte[] { 10, 77, 3 }, new VirtualNetwork { Subnet = "10.77.3.0" }.Prefix);
        Assert.Null(new VirtualNetwork { Subnet = "pas une adresse" }.Prefix);
    }
}
