using System.Buffers.Binary;
using System.Net.NetworkInformation;

namespace WinQEMU.Core.Networking
{
    /// <summary>
    /// Puts the machines of an external network on the physical network under this PC's MAC address, as
    /// VMware's bridge does on Wi-Fi: an access point accepts only the MAC of the station that associated
    /// with it, and drops frames from any other. Each machine keeps its own IP address (from the network's
    /// DHCP server); frames coming back to this PC's MAC are handed to the machine that owns their IP.
    /// </summary>
    /// <remarks>
    /// Done on Ethernet too: the card then stays out of promiscuous mode, and one way works everywhere.
    /// Only IPv4 and ARP cross; IPv6 is dropped both ways, since its neighbour discovery carries MAC
    /// addresses inside checksummed ICMPv6 that would all need rewriting — without it, a machine that
    /// heard router advertisements would try IPv6 first and wait on every connection.
    /// This PC and its own machines cannot reach each other through it: Windows does not receive what is
    /// injected on its own card, and what it sends is told apart from the machines' frames only by its
    /// MAC. Machines reach this PC through their NAT card or an internal network instead.
    /// </remarks>
    public sealed class MacTranslator
    {
        private const ushort Ipv4 = 0x0800;
        private const ushort Arp = 0x0806;
        private const int EthernetHeader = 14;

        private readonly byte[] _host;
        private readonly Dictionary<uint, PhysicalAddress> _machineOf = new();
        private readonly HashSet<PhysicalAddress> _machines = new();
        private readonly object _lock = new();

        public MacTranslator(PhysicalAddress host)
        {
            _host = host.GetAddressBytes();
            if (_host.Length != 6)
                throw new ArgumentException("An Ethernet MAC address has 6 bytes.", nameof(host));
        }

        /// <summary>
        /// A frame from a machine, as it must go out on the card; null when it must not leave (IPv6, frames
        /// for this PC, anything but IPv4 and ARP).
        /// </summary>
        public byte[]? Outgoing(byte[] frame)
        {
            if (frame.Length < EthernetHeader || IsHost(frame, 0))
                return null;
            var machine = new PhysicalAddress(frame[6..12]);
            if ((frame[6] & 1) != 0)
                return null;   // a group address is never a sender

            var copy = (byte[])frame.Clone();
            switch (EtherType(copy))
            {
                case Arp when copy.Length >= EthernetHeader + 28 && IsEthernetIpv4Arp(copy):
                    var arp = EthernetHeader;
                    if (copy.AsSpan(arp + 8, 6).SequenceEqual(frame.AsSpan(6, 6)))
                        _host.CopyTo(copy, arp + 8);
                    Learn(BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(arp + 14)), machine);
                    break;

                case Ipv4 when copy.Length >= EthernetHeader + 20:
                    Learn(BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(EthernetHeader + 12)), machine);
                    if (DhcpOffset(copy, toServer: true) is { } bootp)
                    {
                        // The server would answer the client's MAC, which never reaches this PC: ask it to
                        // broadcast. The UDP checksum, now wrong, is dropped (0 means none in IPv4).
                        copy[bootp + 10] |= 0x80;
                        var udp = bootp - 8;
                        copy[udp + 6] = 0;
                        copy[udp + 7] = 0;
                    }
                    break;

                default:
                    return null;
            }

            lock (_lock)
                _machines.Add(machine);
            _host.CopyTo(copy, 6);
            return copy;
        }

        /// <summary>
        /// A frame seen on the card, as it must reach the machines: broadcasts as they are, frames sent to
        /// this PC's MAC for a machine's IP readdressed to that machine. Null when it is not for them
        /// (this PC's own traffic, IPv6, what is addressed to this PC).
        /// </summary>
        public byte[]? Incoming(byte[] frame)
        {
            // This PC's own frames, and the machines' once sent: the capture sees both directions.
            if (frame.Length < EthernetHeader || IsHost(frame, 6))
                return null;

            var type = EtherType(frame);
            if (type is not (Ipv4 or Arp))
                return null;
            if ((frame[0] & 1) != 0)
                return frame;
            if (!IsHost(frame, 0))
                return null;   // unicast to another station: only seen when something else turned promiscuous mode on

            PhysicalAddress? machine = null;
            var copy = (byte[])frame.Clone();
            if (type == Arp && copy.Length >= EthernetHeader + 28 && IsEthernetIpv4Arp(copy))
            {
                machine = MachineOf(BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(EthernetHeader + 24)));
                if (machine is not null && IsHost(copy, EthernetHeader + 18))
                    machine.GetAddressBytes().CopyTo(copy, EthernetHeader + 18);
            }
            else if (type == Ipv4 && copy.Length >= EthernetHeader + 20)
            {
                machine = MachineOf(BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(EthernetHeader + 16)));

                // A server that ignored the broadcast flag answers a machine without an address yet: the
                // client's MAC in the reply says whose it is.
                if (machine is null && DhcpOffset(copy, toServer: false) is { } bootp && copy.Length >= bootp + 34)
                {
                    var client = new PhysicalAddress(copy[(bootp + 28)..(bootp + 34)]);
                    lock (_lock)
                        machine = _machines.Contains(client) ? client : null;
                }
            }

            if (machine is null)
                return null;
            machine.GetAddressBytes().CopyTo(copy, 0);
            return copy;
        }

        private void Learn(uint address, PhysicalAddress machine)
        {
            // 0.0.0.0 is a machine without an address yet (DHCP, ARP probes); a group or broadcast address
            // is never a machine's.
            if (address == 0 || address == uint.MaxValue || address >> 28 == 0xE)
                return;
            lock (_lock)
                _machineOf[address] = machine;
        }

        private PhysicalAddress? MachineOf(uint address)
        {
            lock (_lock)
                return _machineOf.TryGetValue(address, out var machine) ? machine : null;
        }

        /// <summary>The machine that last sent from an address, for display and tests.</summary>
        public PhysicalAddress? MachineAt(uint address) => MachineOf(address);

        private bool IsHost(byte[] frame, int offset) => frame.AsSpan(offset, 6).SequenceEqual(_host);

        private static ushort EtherType(byte[] frame) => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12));

        /// <summary>Hardware type Ethernet, protocol IPv4, 6- and 4-byte addresses: the only ARP there is in practice.</summary>
        private static bool IsEthernetIpv4Arp(byte[] frame) =>
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(EthernetHeader)) == 1
            && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(EthernetHeader + 2)) == Ipv4
            && frame[EthernetHeader + 4] == 6
            && frame[EthernetHeader + 5] == 4;

        /// <summary>Where the BOOTP message of a DHCP packet starts (client to server, or back), or null.</summary>
        private static int? DhcpOffset(byte[] frame, bool toServer)
        {
            var ihl = (frame[EthernetHeader] & 0x0F) * 4;
            var udp = EthernetHeader + ihl;
            if (frame[EthernetHeader] >> 4 != 4 || ihl < 20 || frame[EthernetHeader + 9] != 17 || frame.Length < udp + 8 + 236)
                return null;
            // A fragment's second part has no UDP header.
            if ((BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(EthernetHeader + 6)) & 0x1FFF) != 0)
                return null;
            var source = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udp));
            var destination = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(udp + 2));
            return (toServer ? source == 68 && destination == 67 : source == 67 && destination == 68) ? udp + 8 : null;
        }
    }
}
