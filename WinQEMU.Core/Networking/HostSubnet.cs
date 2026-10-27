using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinQEMU.Core.Networking
{
    /// <summary>An IPv4 network this PC is on, through one of its cards.</summary>
    public sealed record HostSubnet(uint Network, int PrefixLength, string Card)
    {
        /// <summary>QEMU's NAT, which every machine with a NAT card is on: 10.0.2.0/24.</summary>
        public static HostSubnet QemuNat { get; } = new(0x0A000200, 24, "le NAT de QEMU");

        /// <summary>
        /// The networks of this PC's cards that are up, except the ones named (the TAP adapters of
        /// internal networks, which are on their network's subnet by design). Link-local addresses
        /// (169.254, a card without DHCP) are no one's network.
        /// </summary>
        public static IReadOnlyList<HostSubnet> List(IEnumerable<string> exceptCards)
        {
            var excluded = exceptCards.Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = new List<HostSubnet>();
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up
                    || card.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || excluded.Contains(card.Id))
                    continue;
                foreach (var address in card.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.PrefixLength is < 1 or > 32)
                        continue;
                    var value = DhcpScope.Parse(address.Address.ToString())!.Value;
                    if (value >> 16 == 0xA9FE)
                        continue;
                    var mask = uint.MaxValue << (32 - address.PrefixLength);
                    found.Add(new HostSubnet(value & mask, address.PrefixLength, $"la carte « {card.Name} » de ce PC"));
                }
            }
            return found;
        }

        public bool Overlaps(uint network, int prefixLength)
        {
            var shorter = Math.Min(PrefixLength, prefixLength);
            var mask = shorter == 0 ? 0 : uint.MaxValue << (32 - shorter);
            return (Network & mask) == (network & mask);
        }

        public override string ToString() => $"{DhcpScope.Format(Network)}/{PrefixLength}";
    }
}
