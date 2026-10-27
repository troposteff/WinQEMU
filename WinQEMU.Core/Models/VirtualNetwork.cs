using System.Globalization;
using System.Text.Json.Serialization;
using WinQEMU.Core.Networking;

namespace WinQEMU.Core.Models
{
    /// <summary>Who a virtual network connects, as Hyper-V names its switch types.</summary>
    public enum VirtualNetworkType
    {
        /// <summary>The machines on it, and nothing else.</summary>
        Private,
        /// <summary>The machines and this PC.</summary>
        Internal,
        /// <summary>The machines and the physical network.</summary>
        External
    }

    /// <summary>
    /// A named network that machines' cards are plugged into, served by WinQEMU's own switch
    /// (Networking.VirtualSwitch). Kept in the application settings, like VMware's virtual network
    /// editor and Hyper-V's switch manager: networks outlive any one machine.
    /// </summary>
    public class VirtualNetwork : ObservableBase
    {
        private string _id = Guid.NewGuid().ToString("N");
        public string Id { get => _id; set => SetProperty(ref _id, value); }

        private string _name = string.Empty;
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private VirtualNetworkType _type = VirtualNetworkType.Private;
        public VirtualNetworkType Type { get => _type; set => SetProperty(ref _type, value); }

        private string _subnet = "10.77.1.0";
        /// <summary>The network's address, e.g. 10.77.1.0, with <see cref="PrefixLength"/> its mask.</summary>
        public string Subnet { get => _subnet; set { if (SetProperty(ref _subnet, value)) RaiseAddressesChanged(); } }

        private int _prefixLength = 24;
        /// <summary>The mask as a prefix length, 8 to 29: /29 is the smallest leaving room for this PC, the server and a lease.</summary>
        public int PrefixLength { get => _prefixLength; set { if (SetProperty(ref _prefixLength, value)) RaiseAddressesChanged(); } }

        private string _tapAdapterId = string.Empty;
        /// <summary>
        /// For an internal network: the TAP-Windows adapter (its NetCfgInstanceId) that is this PC's card on
        /// it. Networking.TapAdapter lists them.
        /// </summary>
        public string TapAdapterId { get => _tapAdapterId; set => SetProperty(ref _tapAdapterId, value); }

        private string _tapAdapterName = string.Empty;
        /// <summary>The adapter's name when it was chosen, shown even when it has since disappeared.</summary>
        public string TapAdapterName { get => _tapAdapterName; set => SetProperty(ref _tapAdapterName, value); }

        private string _physicalAdapterId = string.Empty;
        /// <summary>
        /// For an external network: the card of this PC (its interface id, as Windows' NetworkInterface
        /// gives it) the machines go out on, through Npcap. Networking.PhysicalAdapter lists them.
        /// </summary>
        public string PhysicalAdapterId { get => _physicalAdapterId; set => SetProperty(ref _physicalAdapterId, value); }

        private string _physicalAdapterName = string.Empty;
        /// <summary>The card's name when it was chosen ("Wi-Fi"), shown even when it has since disappeared.</summary>
        public string PhysicalAdapterName { get => _physicalAdapterName; set { if (SetProperty(ref _physicalAdapterName, value)) RaisePropertyChanged(nameof(SubnetText)); } }

        /// <summary>
        /// Whether the network has addresses of its own. An external network has none: the machines take
        /// theirs from the physical network's DHCP server (the box), like any device on it.
        /// </summary>
        [JsonIgnore]
        public bool HasAddresses => Type != VirtualNetworkType.External;

        private string _dhcpStart = string.Empty;
        /// <summary>
        /// First address leased; empty for the default, which follows the subnet (.100 of a /24). Takes
        /// effect when the network's switch next starts, like every address setting.
        /// </summary>
        public string DhcpStart { get => _dhcpStart; set { if (SetProperty(ref _dhcpStart, value)) RaiseAddressesChanged(); } }

        private string _dhcpEnd = string.Empty;
        /// <summary>Last address leased; empty for the default (.199 of a /24).</summary>
        public string DhcpEnd { get => _dhcpEnd; set { if (SetProperty(ref _dhcpEnd, value)) RaiseAddressesChanged(); } }

        private bool _dhcp = true;
        /// <summary>Whether the switch hands out addresses. Off for networks where a guest runs its own DHCP server.</summary>
        public bool Dhcp { get => _dhcp; set { if (SetProperty(ref _dhcp, value)) RaisePropertyChanged(nameof(SubnetText)); } }

        private void RaiseAddressesChanged()
        {
            RaisePropertyChanged(nameof(SubnetText));
            RaisePropertyChanged(nameof(HostAddress));
            RaisePropertyChanged(nameof(DefaultDhcpStart));
            RaisePropertyChanged(nameof(DefaultDhcpEnd));
        }

        [JsonIgnore]
        public string TypeText => Type switch
        {
            VirtualNetworkType.Private => "Privé (les machines entre elles)",
            VirtualNetworkType.Internal => "Interne (les machines et ce PC)",
            _ => "Externe (le réseau physique)"
        };

        [JsonIgnore]
        public string SubnetText => Type == VirtualNetworkType.External
            ? $"sur « {PhysicalAdapterName} », adresses données par le réseau physique (la box)"
            : Problem is { } problem
            ? problem
            : $"{Subnet}/{PrefixLength.ToString(CultureInfo.InvariantCulture)}" +
              (Dhcp && Scope is { } scope ? $", DHCP de {DhcpScope.Format(scope.First)} à {DhcpScope.Format(scope.Last)}" : string.Empty) +
              (Type == VirtualNetworkType.Internal ? $", ce PC en {HostAddress} sur « {TapAdapterName} »" : string.Empty);

        /// <summary>This PC's address on an internal network: the subnet's first usable one.</summary>
        [JsonIgnore]
        public string HostAddress => Network is { } network ? DhcpScope.Format(network + 1) : string.Empty;

        /// <summary>The subnet's address, or null when <see cref="Subnet"/> is not an IPv4 address.</summary>
        [JsonIgnore]
        public uint? Network => DhcpScope.Parse(Subnet);

        private uint Mask => PrefixLength is >= 8 and <= 29 ? uint.MaxValue << (32 - PrefixLength) : uint.MaxValue;

        /// <summary>
        /// What a subnet's range is when nobody set one: .100 to .199 when the subnet has room for them,
        /// as before addresses could be set, else every address between this PC's and the server's.
        /// </summary>
        [JsonIgnore]
        public string DefaultDhcpStart => Network is { } n ? DhcpScope.Format(DefaultRange(n).First) : string.Empty;

        [JsonIgnore]
        public string DefaultDhcpEnd => Network is { } n ? DhcpScope.Format(DefaultRange(n).Last) : string.Empty;

        private (uint First, uint Last) DefaultRange(uint network)
        {
            var server = (network | ~Mask) - 1;
            return network + 199 < server ? (network + 100, network + 199) : (network + 2, server - 1);
        }

        /// <summary>Why the addresses cannot be used, or null when they can.</summary>
        [JsonIgnore]
        public string? Problem
        {
            get
            {
                if (!HasAddresses)
                    return null;
                if (Network is not { } network)
                    return $"« {Subnet} » n'est pas une adresse IPv4 de sous-réseau (exemple : 10.77.1.0).";
                if (PrefixLength is < 8 or > 29)
                    return "Le masque va de /8 (255.0.0.0) à /29 (255.255.255.248).";
                if ((network & ~Mask) != 0)
                    return $"{Subnet} n'est pas le début d'un sous-réseau /{PrefixLength} : ce serait {DhcpScope.Format(network & Mask)}.";
                if (!Dhcp)
                    return null;

                var (defaultFirst, defaultLast) = DefaultRange(network);
                var first = DhcpStart.Length == 0 ? defaultFirst : DhcpScope.Parse(DhcpStart);
                var last = DhcpEnd.Length == 0 ? defaultLast : DhcpScope.Parse(DhcpEnd);
                if (first is null || last is null)
                    return "Les adresses DHCP doivent être des adresses IPv4 complètes (exemple : 10.77.1.100).";

                var server = (network | ~Mask) - 1;
                if (first < network + 2 || last > server - 1)
                    return $"Les adresses DHCP doivent être entre {DhcpScope.Format(network + 2)} et {DhcpScope.Format(server - 1)} : " +
                           $"{HostAddress} est celle de ce PC sur un réseau interne, {DhcpScope.Format(server)} celle du serveur DHCP.";
                return first > last ? "La première adresse DHCP doit précéder la dernière." : null;
            }
        }

        /// <summary>The network's addresses for its switch, or null when <see cref="Problem"/> says why not.</summary>
        [JsonIgnore]
        public DhcpScope? Scope
        {
            get
            {
                if (Problem is not null || Network is not { } network)
                    return null;
                var (defaultFirst, defaultLast) = DefaultRange(network);
                return new DhcpScope(network, PrefixLength,
                    DhcpStart.Length == 0 ? defaultFirst : DhcpScope.Parse(DhcpStart)!.Value,
                    DhcpEnd.Length == 0 ? defaultLast : DhcpScope.Parse(DhcpEnd)!.Value);
            }
        }

        /// <summary>
        /// Why the subnet cannot sit beside the others, or null when it can. Two virtual networks never share
        /// addresses, as VMware's editor refuses two VMnets on one subnet: a machine on both, or this PC on
        /// an internal one, could not tell them apart. An internal network also puts this PC on its subnet,
        /// so it must stay off this PC's own networks (its Wi-Fi, which an external network uses too), or
        /// Windows would route part of that network into the virtual one. Any network overlapping QEMU's
        /// NAT would do the same inside a guest with both cards.
        /// </summary>
        public string? ConflictWith(IEnumerable<VirtualNetwork> networks, IEnumerable<HostSubnet> hostSubnets)
        {
            if (!HasAddresses || Network is not { } network || Problem is not null)
                return null;

            if (HostSubnet.QemuNat.Overlaps(network, PrefixLength))
                return $"{SubnetLabel} chevauche {HostSubnet.QemuNat.Card} ({HostSubnet.QemuNat}) : choisissez un autre sous-réseau.";

            foreach (var other in networks)
            {
                if (other == this || !other.HasAddresses || other.Problem is not null || other.Network is not { } theirs)
                    continue;
                if (new HostSubnet(theirs, other.PrefixLength, other.Name).Overlaps(network, PrefixLength))
                    return $"{SubnetLabel} chevauche celui du réseau {other.Name} ({other.Subnet}/{other.PrefixLength.ToString(CultureInfo.InvariantCulture)}) : " +
                           "deux réseaux virtuels ne peuvent pas partager des adresses. Choisissez un autre sous-réseau.";
            }
            if (Type != VirtualNetworkType.Internal)
                return null;
            foreach (var host in hostSubnets)
            {
                if (host.Overlaps(network, PrefixLength))
                    return $"{SubnetLabel} chevauche le réseau de {host.Card} ({host}) : ce PC ne saurait plus par où joindre ces adresses.";
            }
            return null;
        }

        private string SubnetLabel => $"Le sous-réseau {Subnet}/{PrefixLength.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// <see cref="ConflictWith"/> against this PC's cards as they are now, the internal networks' own TAP
        /// adapters aside (they are on their subnet by design).
        /// </summary>
        public string? ConflictAmong(IReadOnlyCollection<VirtualNetwork> networks) =>
            ConflictWith(networks, HostSubnet.List(networks.Select(n => n.TapAdapterId)));

        /// <summary>10.77.N.0 with the first N no other network uses.</summary>
        public static string FreeSubnet(IEnumerable<VirtualNetwork> existing)
        {
            var used = existing.Select(n => n.Subnet).ToHashSet();
            for (var n = 1; n < 255; n++)
            {
                var candidate = $"10.77.{n.ToString(CultureInfo.InvariantCulture)}.0";
                if (!used.Contains(candidate))
                    return candidate;
            }
            return "10.77.254.0";
        }
    }
}
