using System.Globalization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Networking
{
    /// <summary>
    /// The switches of the virtual networks, started when a machine plugged into one starts and kept for
    /// as long as WinQEMU runs: a machine's card reconnects by itself (reconnect-ms), so the network
    /// survives machines coming and going, and leases stay put.
    /// </summary>
    public sealed class VirtualNetworkHost : IAsyncDisposable
    {
        private readonly Dictionary<string, VirtualSwitch> _switches = new();
        /// <summary>The settings each switch was started with (<see cref="Signature"/>).</summary>
        private readonly Dictionary<string, string> _started = new();
        private readonly object _lock = new();

        /// <summary>What a switch is started from: a change here needs a new one.</summary>
        private static string Signature(VirtualNetwork network) => string.Join("|",
            network.Type, network.Subnet, network.PrefixLength, network.Dhcp, network.DhcpStart, network.DhcpEnd,
            network.TapAdapterId, network.PhysicalAdapterId);

        /// <summary>
        /// The loopback port of the network's switch, started on first use. <paramref name="all"/> are every
        /// network, for the subnet checks: this PC may have joined since a Wi-Fi on the same subnet.
        /// </summary>
        public int PortOf(VirtualNetwork network, IReadOnlyCollection<VirtualNetwork> all)
        {
            lock (_lock)
            {
                if (_switches.TryGetValue(network.Id, out var running))
                {
                    // Kept while it serves as it was set up. Otherwise started again once no machine is on it: its
                    // settings changed (subnet, DHCP, card), or it lost this PC (TAP adapter disabled, Wi-Fi gone).
                    // Before, a running switch was kept until WinQEMU restarted, whatever was changed.
                    if (_started.TryGetValue(network.Id, out var how) && how == Signature(network) && !running.LostUplink
                        || running.MachinePorts > 0)
                        return running.Port;
                    _switches.Remove(network.Id);
                    _ = running.DisposeAsync().AsTask();
                }

                _started[network.Id] = Signature(network);

                if (network.Type == VirtualNetworkType.External)
                    return StartExternal(network);

                var scope = network.Scope ?? throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, PlatformText.NetworkAddressesInvalid, network.Name, network.Problem));
                if (network.ConflictAmong(all) is { } conflict)
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, PlatformText.NetworkCannotStart, network.Name, conflict));

                // An internal network is this PC's too: its TAP adapter becomes one more port, and the
                // DHCP server keeps .1 for it. Opened before anything starts, so a missing or refused
                // adapter stops the machine with the reason instead of leaving a network without the PC.
                TapDevice? device = null;
                var reserved = new Dictionary<System.Net.NetworkInformation.PhysicalAddress, uint>();
                if (network.Type == VirtualNetworkType.Internal)
                {
                    var adapter = TapAdapter.List().FirstOrDefault(a => a.Id.Equals(network.TapAdapterId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            string.Format(CultureInfo.CurrentCulture, PlatformText.NetworkTapAdapterMissing, network.Name, network.TapAdapterName));
                    device = TapDevice.Open(adapter);
                    if (adapter.Mac is { } mac)
                        reserved[mac] = scope.HostAddress;
                }

                var created = new VirtualSwitch(network.Dhcp ? new DhcpResponder(scope, reserved) : null);
                created.Start();
                if (device is not null)
                    created.Attach(device);
                _switches[network.Id] = created;
                return created.Port;
            }
        }

        /// <summary>
        /// An external network's switch, with the physical card as one more port and no DHCP server of
        /// its own: the machines ask the physical network's. The card is opened before anything starts,
        /// so a missing Npcap or card stops the machine with the reason.
        /// </summary>
        private int StartExternal(VirtualNetwork network)
        {
            if (!Npcap.IsInstalled)
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, PlatformText.NetworkNpcapMissing, network.Name));
            var adapter = PhysicalAdapter.List().FirstOrDefault(a => a.Id.Equals(network.PhysicalAdapterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, PlatformText.NetworkPhysicalAdapterMissing, network.PhysicalAdapterName, network.Name));

            var device = Npcap.Open(adapter);
            var created = new VirtualSwitch(null);
            created.Start();
            created.Attach(device, new MacTranslator(adapter.Mac, HostAddresses(adapter.Id)));
            _switches[network.Id] = created;
            return created.Port;
        }

        /// <summary>
        /// Whether an IPv4 address is one of this PC's on the card (MacTranslator), read again at most every few
        /// seconds: the card may take another address (DHCP renewal, another Wi-Fi) while the switch runs.
        /// </summary>
        private static Func<uint, bool> HostAddresses(string adapterId)
        {
            HashSet<uint> addresses = new();
            var readAt = DateTime.MinValue;
            var gate = new object();
            return address =>
            {
                lock (gate)
                {
                    if (DateTime.UtcNow - readAt > TimeSpan.FromSeconds(5))
                    {
                        readAt = DateTime.UtcNow;
                        try
                        {
                            addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                                .Where(n => n.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
                                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                                .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                .Select(u => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(u.Address.GetAddressBytes()))
                                .ToHashSet();
                        }
                        catch (System.Net.NetworkInformation.NetworkInformationException)
                        {
                            // Kept as they were.
                        }
                    }
                    return addresses.Contains(address);
                }
            };
        }

        /// <summary>The address the network's DHCP gave a card, for display. Null when none, or not yet.</summary>
        public string? LeaseOf(string networkId, string macAddress)
        {
            VirtualSwitch? found;
            lock (_lock)
                _switches.TryGetValue(networkId, out found);
            if (found?.Dhcp is not { } dhcp
                || !System.Net.NetworkInformation.PhysicalAddress.TryParse(macAddress.Replace(':', '-').ToUpperInvariant(), out var mac))
                return null;
            return dhcp.LeaseOf(mac);
        }

        /// <summary>Stops a network's switch, cutting off the machines on it (deletion, or a subnet change).</summary>
        public async Task StopAsync(string networkId)
        {
            VirtualSwitch? removed;
            lock (_lock)
                _switches.Remove(networkId, out removed);
            if (removed is not null)
                await removed.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            List<VirtualSwitch> all;
            lock (_lock)
            {
                all = _switches.Values.ToList();
                _switches.Clear();
            }
            foreach (var running in all)
                await running.DisposeAsync().ConfigureAwait(false);
        }
    }
}
