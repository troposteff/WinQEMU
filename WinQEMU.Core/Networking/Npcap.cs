using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Networking
{
    /// <summary>A card of this PC that an external network can put machines on.</summary>
    public sealed record PhysicalAdapter(string Id, string Name, string Description, PhysicalAddress Mac, bool IsWireless, bool HasGateway)
    {
        // Virtual cards of other software: bridging onto them would not reach the physical network.
        private static readonly string[] Virtual =
            { "Hyper-V", "TAP-Windows", "Virtual", "VPN", "Fortinet", "Check Point", "Barracuda", "OpenVPN", "WAN Miniport", "Bluetooth", "Loopback", "Npcap" };

        /// <summary>Wi-Fi and Ethernet cards that are up, those with a default gateway first.</summary>
        public static IReadOnlyList<PhysicalAdapter> List() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                            && !Virtual.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new PhysicalAdapter(n.Id, n.Name, n.Description, n.GetPhysicalAddress(),
                    n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)))
                .OrderByDescending(a => a.HasGateway)
                .ToList();
    }

    /// <summary>
    /// Npcap, the packet driver Wireshark uses: what lets WinQEMU send and receive raw Ethernet frames on
    /// a physical card, as VMware's own bridge driver does, without touching Windows' network settings.
    /// </summary>
    /// <remarks>
    /// The user installs it (its licence forbids bundling it). Windows' built-in NDISUIO was tried first:
    /// it opens the Wi-Fi card, but refuses any ether-type filter and any write ("invalid parameter"),
    /// being kept for 802.1X. A kernel driver of our own would need an EV certificate and Microsoft's
    /// attestation signing.
    /// </remarks>
    public static class Npcap
    {
        private static readonly string Folder = Path.Combine(Environment.SystemDirectory, "Npcap");

        public const string DownloadPage = "https://npcap.com/#download";

        public static bool IsInstalled => File.Exists(Path.Combine(Folder, "wpcap.dll"));

        private static bool _loaded;

        /// <summary>Npcap's DLLs live in System32\Npcap, outside the search path, unless made findable.</summary>
        private static void Load()
        {
            if (_loaded)
                return;
            if (!IsInstalled)
                throw new InvalidOperationException(PlatformText.NpcapNotInstalled);
            SetDllDirectoryW(Folder);
            NativeLibrary.Load(Path.Combine(Folder, "Packet.dll"));
            NativeLibrary.Load(Path.Combine(Folder, "wpcap.dll"));
            _loaded = true;
        }

        /// <summary>Opens a card for sending and receiving, not in promiscuous mode: the bridge only needs what is addressed to this PC and broadcasts.</summary>
        public static PcapDevice Open(PhysicalAdapter adapter)
        {
            Load();
            var errors = new byte[256];
            var handle = pcap_create($@"\Device\NPF_{adapter.Id}", errors);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, PlatformText.NpcapCannotOpen, adapter.Name, Text(errors)));

            pcap_set_snaplen(handle, 65536);
            pcap_set_promisc(handle, 0);
            pcap_set_timeout(handle, 100);
            pcap_set_immediate_mode(handle, 1);
            var status = pcap_activate(handle);
            if (status < 0)
            {
                var message = Marshal.PtrToStringAnsi(pcap_geterr(handle)) ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                pcap_close(handle);
                throw new InvalidOperationException(message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    ? PlatformText.NpcapAdminOnly
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, PlatformText.NpcapCannotActivate, adapter.Name, message));
            }
            return new PcapDevice(handle);
        }

        private static string Text(byte[] buffer) => System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string path);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr pcap_create(string source, byte[] errors);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pcap_set_snaplen(IntPtr handle, int length);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pcap_set_promisc(IntPtr handle, int on);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pcap_set_timeout(IntPtr handle, int milliseconds);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pcap_set_immediate_mode(IntPtr handle, int on);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pcap_activate(IntPtr handle);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pcap_geterr(IntPtr handle);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pcap_next_ex(IntPtr handle, out IntPtr header, out IntPtr data);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pcap_sendpacket(IntPtr handle, byte[] buffer, int size);

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pcap_close(IntPtr handle);
    }

    /// <summary>A card opened through Npcap.</summary>
    public sealed class PcapDevice : IDisposable
    {
        private IntPtr _handle;
        private readonly object _sendLock = new();
        // Closing the handle while pcap_next_ex waits on it would free what it is reading into: closing
        // waits for the read in progress, which returns within the read timeout.
        private readonly object _readLock = new();

        internal PcapDevice(IntPtr handle) => _handle = handle;

        /// <summary>The next frame seen on the card, or null after the read timeout (100 ms) with none.</summary>
        public byte[]? Read()
        {
            lock (_readLock)
                return ReadLocked();
        }

        private byte[]? ReadLocked()
        {
            var handle = _handle;
            if (handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(PcapDevice));
            var status = Npcap.pcap_next_ex(handle, out var header, out var data);
            if (status == 0)
                return null;
            if (status < 0)
                throw new IOException(Marshal.PtrToStringAnsi(Npcap.pcap_geterr(handle)) ?? "pcap_next_ex failed");

            // struct pcap_pkthdr { struct timeval ts (2 x 32 bits on Windows); bpf_u_int32 caplen; bpf_u_int32 len; }
            var captured = Marshal.ReadInt32(header, 8);
            var frame = new byte[captured];
            Marshal.Copy(data, frame, 0, captured);
            return frame;
        }

        public void Send(byte[] frame)
        {
            lock (_sendLock)
            {
                if (_handle == IntPtr.Zero)
                    throw new ObjectDisposedException(nameof(PcapDevice));
                if (Npcap.pcap_sendpacket(_handle, frame, frame.Length) != 0)
                    throw new IOException(Marshal.PtrToStringAnsi(Npcap.pcap_geterr(_handle)) ?? "pcap_sendpacket failed");
            }
        }

        public void Dispose()
        {
            lock (_readLock)
            lock (_sendLock)
            {
                var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero)
                    Npcap.pcap_close(handle);
            }
        }
    }
}
