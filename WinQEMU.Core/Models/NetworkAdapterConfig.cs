using System.Collections.ObjectModel;

namespace WinQEMU.Core.Models
{
    public class PortForward : ObservableBase
    {
        private PortForwardProtocol _protocol = PortForwardProtocol.Tcp;
        public PortForwardProtocol Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }

        private int _hostPort;
        public int HostPort { get => _hostPort; set => SetProperty(ref _hostPort, value); }

        private int _guestPort;
        public int GuestPort { get => _guestPort; set => SetProperty(ref _guestPort, value); }

        private string _hostAddress = "127.0.0.1";
        /// <summary>Host interface to listen on. Keep the loopback default unless the port must be reachable from the LAN.</summary>
        public string HostAddress { get => _hostAddress; set => SetProperty(ref _hostAddress, value); }
    }

    public class NetworkAdapterConfig : ObservableBase
    {
        private string _id = "net0";
        public string Id { get => _id; set => SetProperty(ref _id, value); }

        private NetworkMode _mode = NetworkMode.Nat;
        public NetworkMode Mode { get => _mode; set { if (SetProperty(ref _mode, value)) RaisePropertyChanged(nameof(Connection)); } }

        private bool _connected = true;
        /// <summary>
        /// Whether the cable is plugged in, as VMware's "Connected": unplugged, the card stays in the guest
        /// (same name, same configuration) but sees no link, by QMP set_link at start and while running.
        /// </summary>
        public bool Connected { get => _connected; set => SetProperty(ref _connected, value); }

        private NicModel _model = NicModel.VirtioNet;
        public NicModel Model { get => _model; set => SetProperty(ref _model, value); }

        private string _macAddress = string.Empty;
        /// <summary>Empty means QEMU generates one. A fixed MAC keeps DHCP leases stable across restarts.</summary>
        public string MacAddress { get => _macAddress; set => SetProperty(ref _macAddress, value); }

        private string _bridgeInterface = string.Empty;
        /// <summary>Name of the host TAP interface, used by <see cref="NetworkMode.Bridged"/>.</summary>
        public string BridgeInterface { get => _bridgeInterface; set => SetProperty(ref _bridgeInterface, value); }

        private int _internalSegment = 1;
        /// <summary>Segment number for <see cref="NetworkMode.Internal"/>; adapters sharing a number share a network.</summary>
        public int InternalSegment { get => _internalSegment; set => SetProperty(ref _internalSegment, value); }

        private string _networkId = string.Empty;
        /// <summary>The <see cref="VirtualNetwork"/> a <see cref="NetworkMode.Network"/> card is plugged into.</summary>
        public string NetworkId { get => _networkId; set { if (SetProperty(ref _networkId, value)) RaisePropertyChanged(nameof(Connection)); } }

        /// <summary>
        /// What the card is plugged into, as one value: "nat", "none", or "net:" and a network's id. The
        /// interface offers NAT and the virtual networks side by side in a single list, as VMware lists
        /// NAT, its bridge and its VMnets, instead of a mode and then a network.
        /// </summary>
        /// <remarks>
        /// Setting anything else is ignored: a list whose items are being replaced sets null for a moment,
        /// which must not unplug the card.
        /// </remarks>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Connection
        {
            get => Mode switch
            {
                NetworkMode.Nat => NatConnection,
                NetworkMode.None => NoConnection,
                NetworkMode.Network => NetworkConnection(NetworkId),
                _ => Mode.ToString()
            };
            set
            {
                if (value == NatConnection)
                    Mode = NetworkMode.Nat;
                else if (value == NoConnection)
                    Mode = NetworkMode.None;
                else if (value is not null && value.StartsWith(NetworkPrefix, StringComparison.Ordinal) && value.Length > NetworkPrefix.Length)
                {
                    NetworkId = value[NetworkPrefix.Length..];
                    Mode = NetworkMode.Network;
                }
            }
        }

        public const string NatConnection = "nat";
        public const string NoConnection = "none";
        private const string NetworkPrefix = "net:";

        public static string NetworkConnection(string networkId) => NetworkPrefix + networkId;

        public ObservableCollection<PortForward> PortForwards { get; set; } = new();

        /// <summary>
        /// Builds a locally-administered MAC in QEMU's registered 52:54:00 range so guests
        /// see a stable address without colliding with real hardware.
        /// </summary>
        public static string GenerateMacAddress()
        {
            var bytes = new byte[3];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return $"52:54:00:{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}";
        }
    }
}
