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
        public NetworkMode Mode { get => _mode; set => SetProperty(ref _mode, value); }

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
