using System.Buffers.Binary;
using System.Net.NetworkInformation;

namespace WinQEMU.Core.Networking
{
    /// <summary>
    /// The DHCP server of a virtual network, answering from inside the switch: DISCOVER, REQUEST, and ARP
    /// for its own address. Leases .100-.199 of the network's /24, the same address to the same card for
    /// as long as WinQEMU runs, and the first free one from a hash of the MAC otherwise, so a machine
    /// usually keeps its address across restarts of the application too.
    /// </summary>
    /// <remarks>
    /// Its own address is .254: .1 is left for this PC on an internal network. No router and no DNS are
    /// announced — the network does not lead anywhere — so a guest's NAT card keeps the default route.
    /// </remarks>
    public sealed class DhcpResponder
    {
        private const int First = 100, Last = 199;
        private const uint LeaseSeconds = 86400;

        /// <summary>A locally administered MAC no QEMU card gets (QEMU uses 52:54:00).</summary>
        public static readonly byte[] Mac = { 0x02, 0x57, 0x51, 0x00, 0x00, 0xFE };

        private readonly byte[] _prefix;
        private readonly Dictionary<string, byte> _leases = new();
        private readonly object _lock = new();

        public DhcpResponder(byte[] prefix)
        {
            _prefix = prefix;
        }

        private byte[] ServerAddress => new[] { _prefix[0], _prefix[1], _prefix[2], (byte)254 };

        /// <summary>The address leased to a MAC, if any.</summary>
        public byte[]? LeaseOf(PhysicalAddress mac)
        {
            lock (_lock)
                return _leases.TryGetValue(mac.ToString(), out var host) ? new[] { _prefix[0], _prefix[1], _prefix[2], host } : null;
        }

        /// <summary>The reply to a frame from a guest, or null when it is not for this server.</summary>
        public byte[]? Answer(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 42)
                return null;
            var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
            if (etherType == 0x0806)
                return AnswerArp(frame);
            if (etherType != 0x0800 || frame[23] != 17)   // IPv4 / UDP
                return null;

            var ipHeader = (frame[14] & 0x0F) * 4;
            var udp = 14 + ipHeader;
            if (frame.Length < udp + 8 + 240 || BinaryPrimitives.ReadUInt16BigEndian(frame[(udp + 2)..]) != 67)
                return null;
            return AnswerDhcp(frame, udp + 8);
        }

        private byte[]? AnswerArp(ReadOnlySpan<byte> frame)
        {
            // Request (op 1) for our address: guests check it before renewing, some before accepting.
            if (BinaryPrimitives.ReadUInt16BigEndian(frame[20..]) != 1 || !frame.Slice(38, 4).SequenceEqual(ServerAddress))
                return null;

            var reply = new byte[42];
            frame[6..12].CopyTo(reply);                 // to the asker
            Mac.CopyTo(reply, 6);
            reply[12] = 0x08; reply[13] = 0x06;
            frame.Slice(14, 6).CopyTo(reply.AsSpan(14)); // htype, ptype, hlen, plen
            reply[20] = 0; reply[21] = 2;               // reply
            Mac.CopyTo(reply, 22);
            ServerAddress.CopyTo(reply, 28);
            frame.Slice(22, 10).CopyTo(reply.AsSpan(32)); // the asker's MAC and IP become the target
            return reply;
        }

        private byte[]? AnswerDhcp(ReadOnlySpan<byte> frame, int bootp)
        {
            var message = frame[bootp..];
            if (message[0] != 1 || BinaryPrimitives.ReadUInt32BigEndian(message[236..]) != 0x63825363)   // BOOTREQUEST, magic cookie
                return null;

            var clientMac = new PhysicalAddress(message.Slice(28, 6).ToArray());
            byte type = 0;
            byte[]? requested = null;
            byte[]? serverId = null;
            for (var i = 240; i < message.Length && message[i] != 255;)
            {
                if (message[i] == 0) { i++; continue; }
                if (i + 1 >= message.Length) break;
                var code = message[i];
                var length = message[i + 1];
                if (i + 2 + length > message.Length) break;
                var value = message.Slice(i + 2, length);
                if (code == 53 && length == 1) type = value[0];
                if (code == 50 && length == 4) requested = value.ToArray();
                if (code == 54 && length == 4) serverId = value.ToArray();
                i += 2 + length;
            }

            switch (type)
            {
                case 1:   // DISCOVER
                    return Reply(message, 2, Lease(clientMac));
                case 3:   // REQUEST
                    // Another server's offer was chosen: say nothing.
                    if (serverId is not null && !serverId.AsSpan().SequenceEqual(ServerAddress))
                        return null;
                    var lease = Lease(clientMac);
                    var asked = requested ?? message.Slice(12, 4).ToArray();   // ciaddr when renewing
                    return asked[3] == lease && asked.AsSpan(0, 3).SequenceEqual(_prefix)
                        ? Reply(message, 5, lease)
                        : Reply(message, 6, null);   // NAK: the guest starts over and is offered its lease
                default:
                    return null;
            }
        }

        private byte Lease(PhysicalAddress mac)
        {
            lock (_lock)
            {
                var key = mac.ToString();
                if (_leases.TryGetValue(key, out var existing))
                    return existing;

                var taken = _leases.Values.ToHashSet();
                var start = (int)(Fnv(mac.GetAddressBytes()) % (Last - First + 1));
                for (var i = 0; i <= Last - First; i++)
                {
                    var candidate = (byte)(First + (start + i) % (Last - First + 1));
                    if (!taken.Contains(candidate))
                        return _leases[key] = candidate;
                }
                throw new InvalidOperationException("No address left on this network.");
            }
        }

        private static uint Fnv(byte[] bytes)
        {
            var hash = 2166136261u;
            foreach (var b in bytes)
                hash = (hash ^ b) * 16777619u;
            return hash;
        }

        /// <summary>An OFFER (2), ACK (5) or NAK (6), broadcast as clients without an address expect.</summary>
        private byte[] Reply(ReadOnlySpan<byte> request, byte type, byte? host)
        {
            var options = new List<byte> { 53, 1, type, 54, 4 };
            options.AddRange(ServerAddress);
            if (host is not null)
            {
                options.AddRange(new byte[] { 1, 4, 255, 255, 255, 0, 51, 4 });
                options.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(LeaseSeconds)));
            }
            options.Add(255);

            var bootp = new byte[240 + options.Count];
            bootp[0] = 2;                                // BOOTREPLY
            bootp[1] = 1; bootp[2] = 6;                  // Ethernet
            request.Slice(4, 4).CopyTo(bootp.AsSpan(4)); // xid
            request.Slice(10, 2).CopyTo(bootp.AsSpan(10)); // flags
            if (host is { } h)
                new[] { _prefix[0], _prefix[1], _prefix[2], h }.CopyTo(bootp, 16);   // yiaddr
            ServerAddress.CopyTo(bootp, 20);             // siaddr
            request.Slice(28, 16).CopyTo(bootp.AsSpan(28)); // chaddr
            BinaryPrimitives.WriteUInt32BigEndian(bootp.AsSpan(236), 0x63825363);
            options.CopyTo(bootp, 240);

            return Frame(bootp);
        }

        private byte[] Frame(byte[] bootp)
        {
            var udpLength = 8 + bootp.Length;
            var ipLength = 20 + udpLength;
            var frame = new byte[14 + ipLength];
            for (var i = 0; i < 6; i++) frame[i] = 0xFF;   // broadcast
            Mac.CopyTo(frame, 6);
            frame[12] = 0x08; frame[13] = 0x00;

            var ip = frame.AsSpan(14);
            ip[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipLength);
            ip[8] = 64; ip[9] = 17;
            ServerAddress.CopyTo(ip[12..]);
            ip[16] = ip[17] = ip[18] = ip[19] = 255;
            BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip[..20]));

            var udp = ip[20..];
            BinaryPrimitives.WriteUInt16BigEndian(udp, 67);
            BinaryPrimitives.WriteUInt16BigEndian(udp[2..], 68);
            BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)udpLength);
            bootp.CopyTo(udp[8..]);   // UDP checksum 0: optional over IPv4
            return frame;
        }

        private static ushort Checksum(ReadOnlySpan<byte> header)
        {
            uint sum = 0;
            for (var i = 0; i < header.Length; i += 2)
                sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
            while (sum > 0xFFFF)
                sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }
    }
}
