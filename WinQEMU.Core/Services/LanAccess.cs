using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>A service commonly reached in a guest, offered in one click.</summary>
    public sealed record ServicePreset(string Label, PortForwardProtocol Protocol, int GuestPort);

    /// <summary>
    /// Makes a guest on QEMU's NAT reachable from the local network: host forwards listening on every
    /// interface, the address to give out, and the Windows firewall that stands in between.
    /// </summary>
    /// <remarks>
    /// Instead of a bridged network, which this QEMU build cannot offer by itself: its tap backend only
    /// takes TAP-Windows adapters (component id tap0901; OpenVPN Connect's "tap_ovpnconnect" is refused
    /// with "Device 'tap' could not be initialized"), and bridging one to a Wi-Fi card needs a Windows
    /// network bridge that corporate access points and VPN clients routinely defeat.
    /// </remarks>
    public static class LanAccess
    {
        /// <summary>Listening address of a forward reachable from the network; 127.0.0.1 keeps it on this PC.</summary>
        public const string AllInterfaces = "0.0.0.0";

        public static IReadOnlyList<ServicePreset> Presets { get; } = new[]
        {
            new ServicePreset("SSH", PortForwardProtocol.Tcp, 22),
            new ServicePreset(CoreText.LanAccessRemoteDesktop, PortForwardProtocol.Tcp, 3389),
            new ServicePreset("Web (HTTP)", PortForwardProtocol.Tcp, 80),
            new ServicePreset(CoreText.LanAccessHttps, PortForwardProtocol.Tcp, 443)
        };

        public static bool IsOnNetwork(PortForward forward) => forward.HostAddress is AllInterfaces or "";

        /// <summary>
        /// The guest's own port when it is free and not a privileged one Windows itself may want (22,
        /// 80, 3389…), else 10000 above it, else the first free port from 20000. Ports other forwards
        /// already claim count as taken even when their machine is stopped.
        /// </summary>
        public static int ChooseHostPort(int guestPort, IEnumerable<int> taken, Func<int, bool>? isFree = null)
        {
            isFree ??= IsTcpPortFree;
            var used = taken.ToHashSet();
            bool Available(int port) => port is > 0 and <= 65535 && !used.Contains(port) && isFree(port);

            if (guestPort >= 1024 && Available(guestPort))
                return guestPort;
            if (Available(guestPort + 10000))
                return guestPort + 10000;
            for (var port = 20000; port <= 65535; port++)
            {
                if (Available(port))
                    return port;
            }
            throw new InvalidOperationException(CoreText.LanAccessNoFreePort);
        }

        public static bool IsTcpPortFree(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>
        /// This PC's IPv4 addresses on the local network: interfaces that are up and have a default
        /// gateway. Hyper-V's virtual switches, VPN adapters and the like have none, or are not up.
        /// </summary>
        public static IReadOnlyList<string> LanAddresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .Select(nic => nic.GetIPProperties())
                .Where(properties => properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                          && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(properties => properties.UnicastAddresses)
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                .Select(address => address.Address.ToString())
                .Distinct()
                .ToList();

        /// <summary>What to type on another machine to reach the service, e.g. "ssh -p 2222 user@192.168.1.20".</summary>
        public static string UsageOf(PortForward forward, string address)
        {
            var port = forward.HostPort.ToString(CultureInfo.InvariantCulture);
            return forward.GuestPort switch
            {
                22 => string.Format(CultureInfo.CurrentCulture, CoreText.LanAccessSshUsage, port, address),
                3389 => $"mstsc /v:{address}:{port}",
                80 => forward.HostPort == 80 ? $"http://{address}" : $"http://{address}:{port}",
                443 => forward.HostPort == 443 ? $"https://{address}" : $"https://{address}:{port}",
                _ => $"{address}:{port}"
            };
        }

        public static string LabelOf(PortForward forward) =>
            Presets.FirstOrDefault(p => p.GuestPort == forward.GuestPort && p.Protocol == forward.Protocol)?.Label
            ?? $"Port {forward.GuestPort.ToString(CultureInfo.InvariantCulture)}{(forward.Protocol == PortForwardProtocol.Udp ? " (UDP)" : string.Empty)}";

        /// <summary>The host-forward rule for QEMU's hostfwd_add, e.g. "tcp:0.0.0.0:2222-:22".</summary>
        public static string RuleOf(PortForward forward) =>
            $"{(forward.Protocol == PortForwardProtocol.Udp ? "udp" : "tcp")}:{(forward.HostAddress.Length == 0 ? AllInterfaces : forward.HostAddress)}:" +
            $"{forward.HostPort.ToString(CultureInfo.InvariantCulture)}-:{forward.GuestPort.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>The same rule as hostfwd_remove wants it: without the guest side.</summary>
        public static string RemovalRuleOf(PortForward forward) =>
            $"{(forward.Protocol == PortForwardProtocol.Udp ? "udp" : "tcp")}:{(forward.HostAddress.Length == 0 ? AllInterfaces : forward.HostAddress)}:" +
            forward.HostPort.ToString(CultureInfo.InvariantCulture);

        /// <summary>Kept in French whatever the interface's language: the rule lives on in Windows' firewall.</summary>
        private const string FirewallRuleName = "WinQEMU - QEMU (accès depuis le réseau)";

        /// <summary>
        /// Whether Windows' firewall lets the network reach QEMU: off on the active profiles, or an enabled
        /// inbound rule allowing this executable. Null when the firewall could not be asked.
        /// </summary>
        /// <remarks>
        /// Read through the firewall's COM API, which needs no administrator. Without such a rule a
        /// forward on 0.0.0.0 listens but nothing from the network ever gets through; Windows' own prompt
        /// to allow QEMU is easily missed, and refused for a standard user.
        /// </remarks>
        public static bool? IsQemuAllowed(string qemuPath)
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type is null)
                    return null;
                dynamic policy = Activator.CreateInstance(type)!;

                int profiles = policy.CurrentProfileTypes;
                var enabled = false;
                foreach (var profile in new[] { 1, 2, 4 })
                {
                    if ((profiles & profile) != 0 && (bool)policy.FirewallEnabled[profile])
                        enabled = true;
                }
                if (!enabled)
                    return true;

                foreach (dynamic rule in policy.Rules)
                {
                    if ((bool)rule.Enabled && (int)rule.Direction == 1 && (int)rule.Action == 1
                        && ((int)rule.Profiles & profiles) != 0
                        && string.Equals((string?)rule.ApplicationName, qemuPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return null;
            }
        }

        /// <summary>
        /// Adds an inbound rule for QEMU on the private and domain profiles (not public: a café's network
        /// has no business reaching the guest). One UAC prompt; OperationCanceledException if declined.
        /// </summary>
        public static async Task<bool> AllowQemuAsync(string qemuPath) =>
            await Elevation.RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow " +
                $"program=\"{qemuPath}\" enable=yes profile=private,domain").ConfigureAwait(false) == 0;
    }
}
