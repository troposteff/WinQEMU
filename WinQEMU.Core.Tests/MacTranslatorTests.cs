using System.Buffers.Binary;
using System.Net.NetworkInformation;
using WinQEMU.Core.Networking;

namespace WinQEMU.Core.Tests;

/// <summary>
/// External networks: machines on the physical network under this PC's MAC, each with its own IP, as
/// VMware bridges onto Wi-Fi.
/// </summary>
public class MacTranslatorTests
{
    private static readonly byte[] Host = { 0x10, 0x91, 0xD1, 0x17, 0xEE, 0xE0 };
    private static readonly byte[] Gateway = { 0xAA, 0xBB, 0xCC, 0x00, 0x00, 0x01 };
    private static readonly byte[] MachineA = { 0x52, 0x54, 0x00, 0x00, 0x00, 0x0A };
    private static readonly byte[] MachineB = { 0x52, 0x54, 0x00, 0x00, 0x00, 0x0B };
    private static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] AddressA = { 192, 168, 1, 50 };
    private static readonly byte[] AddressB = { 192, 168, 1, 51 };
    private static readonly byte[] GatewayAddress = { 192, 168, 1, 254 };

    private static MacTranslator Translator() => new(new PhysicalAddress(Host));

    private static byte[] Frame(byte[] destination, byte[] source, ushort type, byte[] payload)
    {
        var frame = new byte[14 + payload.Length];
        destination.CopyTo(frame, 0);
        source.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), type);
        payload.CopyTo(frame, 14);
        return frame;
    }

    private static byte[] ArpFrame(byte[] destination, byte[] source, ushort operation, byte[] senderMac, byte[] senderIp, byte[] targetMac, byte[] targetIp)
    {
        var arp = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(arp, 1);
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(2), 0x0800);
        arp[4] = 6; arp[5] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(6), operation);
        senderMac.CopyTo(arp, 8); senderIp.CopyTo(arp, 14);
        targetMac.CopyTo(arp, 18); targetIp.CopyTo(arp, 24);
        return Frame(destination, source, 0x0806, arp);
    }

    private static byte[] Ipv4Frame(byte[] destination, byte[] source, byte[] sourceIp, byte[] destinationIp, byte protocol = 1, byte[]? body = null)
    {
        body ??= new byte[8];
        var ip = new byte[20 + body.Length];
        ip[0] = 0x45; ip[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        sourceIp.CopyTo(ip, 12); destinationIp.CopyTo(ip, 16);
        body.CopyTo(ip, 20);
        return Frame(destination, source, 0x0800, ip);
    }

    /// <summary>A DHCP message inside UDP, client to server or back, with a non-zero checksum.</summary>
    private static byte[] DhcpFrame(byte[] destination, byte[] source, byte[] client, bool toServer, byte[] destinationIp)
    {
        var bootp = new byte[240];
        bootp[0] = (byte)(toServer ? 1 : 2); bootp[1] = 1; bootp[2] = 6;
        client.CopyTo(bootp, 28);
        BinaryPrimitives.WriteUInt32BigEndian(bootp.AsSpan(236), 0x63825363);
        var udp = new byte[8 + bootp.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)(toServer ? 68 : 67));
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), (ushort)(toServer ? 67 : 68));
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort)udp.Length);
        udp[6] = 0x12; udp[7] = 0x34;
        bootp.CopyTo(udp, 8);
        return Ipv4Frame(destination, source, toServer ? new byte[4] : GatewayAddress, destinationIp, 17, udp);
    }

    [Fact]
    public void A_machines_frames_leave_under_this_PCs_MAC()
    {
        var sent = Translator().Outgoing(Ipv4Frame(Gateway, MachineA, AddressA, GatewayAddress))!;

        Assert.Equal(Host, sent[6..12]);
        Assert.Equal(Gateway, sent[..6]);
        Assert.Equal(AddressA, sent[26..30]);
    }

    [Fact]
    public void An_ARP_request_announces_this_PCs_MAC_for_the_machines_IP()
    {
        var sent = Translator().Outgoing(ArpFrame(Broadcast, MachineA, 1, MachineA, AddressA, new byte[6], GatewayAddress))!;

        Assert.Equal(Host, sent[6..12]);
        Assert.Equal(Host, sent[22..28]);
        Assert.Equal(AddressA, sent[28..32]);
    }

    [Fact]
    public void Answers_to_this_PCs_MAC_reach_the_machine_that_owns_the_IP()
    {
        var translator = Translator();
        translator.Outgoing(Ipv4Frame(Gateway, MachineA, AddressA, GatewayAddress));
        translator.Outgoing(Ipv4Frame(Gateway, MachineB, AddressB, GatewayAddress));

        var toA = translator.Incoming(Ipv4Frame(Host, Gateway, GatewayAddress, AddressA))!;
        var toB = translator.Incoming(Ipv4Frame(Host, Gateway, GatewayAddress, AddressB))!;

        Assert.Equal(MachineA, toA[..6]);
        Assert.Equal(MachineB, toB[..6]);
        Assert.Equal(Gateway, toA[6..12]);
    }

    [Fact]
    public void An_ARP_reply_is_readdressed_to_the_machine_that_asked()
    {
        var translator = Translator();
        translator.Outgoing(ArpFrame(Broadcast, MachineA, 1, MachineA, AddressA, new byte[6], GatewayAddress));

        var reply = translator.Incoming(ArpFrame(Host, Gateway, 2, Gateway, GatewayAddress, Host, AddressA))!;

        Assert.Equal(MachineA, reply[..6]);
        Assert.Equal(MachineA, reply[32..38]);
        Assert.Equal(Gateway, reply[22..28]);
    }

    [Fact]
    public void What_is_addressed_to_this_PC_itself_stays_out_of_the_machines()
    {
        var translator = Translator();
        translator.Outgoing(Ipv4Frame(Gateway, MachineA, AddressA, GatewayAddress));

        Assert.Null(translator.Incoming(Ipv4Frame(Host, Gateway, GatewayAddress, new byte[] { 192, 168, 1, 170 })));
    }

    [Fact]
    public void This_PCs_own_frames_and_the_machines_once_sent_are_not_looped_back()
    {
        var translator = Translator();
        var sent = translator.Outgoing(ArpFrame(Broadcast, MachineA, 1, MachineA, AddressA, new byte[6], GatewayAddress))!;

        Assert.Null(translator.Incoming(sent));
        Assert.Null(translator.Incoming(Ipv4Frame(Gateway, Host, new byte[] { 192, 168, 1, 170 }, GatewayAddress)));
    }

    [Fact]
    public void Broadcasts_reach_the_machines_unchanged()
    {
        var request = ArpFrame(Broadcast, Gateway, 1, Gateway, GatewayAddress, new byte[6], AddressA);

        Assert.Equal(request, Translator().Incoming(request));
    }

    [Fact]
    public void A_DHCP_request_asks_for_a_broadcast_answer_and_keeps_the_machines_MAC_as_client()
    {
        var sent = Translator().Outgoing(DhcpFrame(Broadcast, MachineA, MachineA, toServer: true, new byte[] { 255, 255, 255, 255 }))!;

        var bootp = 14 + 20 + 8;
        Assert.Equal(0x80, sent[bootp + 10] & 0x80);
        Assert.Equal(MachineA, sent[(bootp + 28)..(bootp + 34)]);
        Assert.Equal(0, sent[14 + 20 + 6]);
        Assert.Equal(0, sent[14 + 20 + 7]);
        Assert.Equal(Host, sent[6..12]);
    }

    [Fact]
    public void A_unicast_DHCP_answer_finds_its_machine_by_the_client_MAC()
    {
        var translator = Translator();
        translator.Outgoing(DhcpFrame(Broadcast, MachineA, MachineA, toServer: true, new byte[] { 255, 255, 255, 255 }));

        var offer = translator.Incoming(DhcpFrame(Host, Gateway, MachineA, toServer: false, AddressA))!;

        Assert.Equal(MachineA, offer[..6]);
    }

    [Fact]
    public void IPv6_crosses_neither_way()
    {
        var translator = Translator();

        Assert.Null(translator.Outgoing(Frame(new byte[] { 0x33, 0x33, 0, 0, 0, 1 }, MachineA, 0x86DD, new byte[40])));
        Assert.Null(translator.Incoming(Frame(new byte[] { 0x33, 0x33, 0, 0, 0, 1 }, Gateway, 0x86DD, new byte[40])));
    }

    [Fact]
    public void A_frame_from_a_machine_to_this_PCs_MAC_does_not_leave()
    {
        Assert.Null(Translator().Outgoing(Ipv4Frame(Host, MachineA, AddressA, new byte[] { 192, 168, 1, 170 })));
    }

    [Fact]
    public void A_machine_without_an_address_yet_is_not_learnt_at_0_0_0_0()
    {
        var translator = Translator();
        translator.Outgoing(DhcpFrame(Broadcast, MachineA, MachineA, toServer: true, new byte[] { 255, 255, 255, 255 }));

        Assert.Null(translator.MachineAt(0));
    }
}
