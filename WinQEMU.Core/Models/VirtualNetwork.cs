using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;

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
        /// <summary>The network's /24, e.g. 10.77.1.0: addresses .100-.199 are leased by DHCP.</summary>
        public string Subnet { get => _subnet; set => SetProperty(ref _subnet, value); }

        private string _tapAdapterId = string.Empty;
        /// <summary>
        /// For an internal network: the TAP-Windows adapter (its NetCfgInstanceId) that is this PC's card on
        /// it. Networking.TapAdapter lists them.
        /// </summary>
        public string TapAdapterId { get => _tapAdapterId; set => SetProperty(ref _tapAdapterId, value); }

        private string _tapAdapterName = string.Empty;
        /// <summary>The adapter's name when it was chosen, shown even when it has since disappeared.</summary>
        public string TapAdapterName { get => _tapAdapterName; set => SetProperty(ref _tapAdapterName, value); }

        private bool _dhcp = true;
        /// <summary>Whether the switch hands out addresses. Off for networks where a guest runs its own DHCP server.</summary>
        public bool Dhcp { get => _dhcp; set => SetProperty(ref _dhcp, value); }

        [JsonIgnore]
        public string TypeText => Type switch
        {
            VirtualNetworkType.Private => "Privé (les machines entre elles)",
            VirtualNetworkType.Internal => "Interne (les machines et ce PC)",
            _ => "Externe (le réseau physique)"
        };

        [JsonIgnore]
        public string SubnetText => Type == VirtualNetworkType.Internal
            ? $"{Subnet}/24{(Dhcp ? ", DHCP" : string.Empty)}, ce PC en {HostAddress} sur « {TapAdapterName} »"
            : $"{Subnet}/24{(Dhcp ? ", DHCP" : string.Empty)}";

        /// <summary>This PC's address on an internal network: .1 of the subnet.</summary>
        [JsonIgnore]
        public string HostAddress => Prefix is { } p ? $"{p[0]}.{p[1]}.{p[2]}.1" : string.Empty;

        /// <summary>The first three bytes of <see cref="Subnet"/>, or null when it is not an IPv4 address.</summary>
        [JsonIgnore]
        public byte[]? Prefix =>
            IPAddress.TryParse(Subnet, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? address.GetAddressBytes()[..3]
                : null;

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
