using WinQEMU.Core.Models;

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
        private readonly object _lock = new();

        /// <summary>The loopback port of the network's switch, started on first use.</summary>
        public int PortOf(VirtualNetwork network)
        {
            lock (_lock)
            {
                if (_switches.TryGetValue(network.Id, out var running))
                    return running.Port;

                var prefix = network.Prefix ?? throw new InvalidOperationException(
                    $"Le réseau {network.Name} a un sous-réseau invalide : {network.Subnet}.");
                var created = new VirtualSwitch(network.Dhcp ? new DhcpResponder(prefix) : null);
                created.Start();
                _switches[network.Id] = created;
                return created.Port;
            }
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
            return dhcp.LeaseOf(mac) is { } lease ? string.Join('.', lease) : null;
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
