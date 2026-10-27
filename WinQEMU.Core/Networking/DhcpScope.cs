using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WinQEMU.Core.Networking
{
    /// <summary>
    /// The addresses of a virtual network: its subnet, this PC's address on it (the first usable one),
    /// the DHCP server's (the last usable one) and the range leased to machines, between the two.
    /// Addresses are host-order integers, so a range is plain arithmetic whatever the mask.
    /// </summary>
    public sealed record DhcpScope(uint Network, int PrefixLength, uint First, uint Last)
    {
        public uint Mask => PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

        public uint Broadcast => Network | ~Mask;

        /// <summary>This PC's address on an internal network: .1 of a /24.</summary>
        public uint HostAddress => Network + 1;

        /// <summary>The DHCP server's address: .254 of a /24.</summary>
        public uint ServerAddress => Broadcast - 1;

        public bool Contains(uint address) => (address & Mask) == Network;

        public static uint? Parse(string text) =>
            IPAddress.TryParse(text.Trim(), out var address) && address.AddressFamily == AddressFamily.InterNetwork
            && text.Trim().Count(c => c == '.') == 3   // "10.77" parses too, as 10.0.0.77
                ? BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes())
                : null;

        public static string Format(uint address) =>
            string.Join('.', BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(address)).Select(b => b.ToString(CultureInfo.InvariantCulture)));

        public static byte[] Bytes(uint address)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
            return bytes;
        }

        /// <summary>255.255.255.0 for 24.</summary>
        public static string MaskText(int prefixLength) =>
            Format(prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength));
    }
}
