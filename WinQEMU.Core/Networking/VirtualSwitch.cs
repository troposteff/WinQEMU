using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinQEMU.Core.Networking
{
    /// <summary>
    /// An Ethernet switch for one virtual network, run by WinQEMU itself. Each machine's card is a QEMU
    /// "stream" netdev connected to it over loopback TCP; frames go to the port that last sent from the
    /// destination MAC, and everything else (broadcast, multicast, unknown) to every other port.
    /// </summary>
    /// <remarks>
    /// Replaces the multicast socket backend the "internal" mode used: that one also sent the guests'
    /// traffic out of the host's network card to anyone joining the same group, and cannot be extended
    /// to reach the host. QEMU frames each packet on the stream with a 4-byte big-endian length.
    /// </remarks>
    public sealed class VirtualSwitch : IAsyncDisposable
    {
        private const int MaxFrame = 65536;

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Dictionary<PhysicalAddress, SwitchPort> _macTable = new();
        private readonly List<SwitchPort> _ports = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly DhcpResponder? _dhcp;
        private Task? _accepting;

        public VirtualSwitch(DhcpResponder? dhcp)
        {
            _dhcp = dhcp;
        }

        /// <summary>The loopback port QEMU connects its cards to.</summary>
        public int Port { get; private set; }

        public DhcpResponder? Dhcp => _dhcp;

        public int ConnectedPorts
        {
            get { lock (_lock) return _ports.Count; }
        }

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync(_stop.Token);
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                client.NoDelay = true;
                var port = new SwitchPort(client);
                lock (_lock)
                    _ports.Add(port);
                _ = ServeAsync(port, cancellationToken);
            }
        }

        private async Task ServeAsync(SwitchPort port, CancellationToken cancellationToken)
        {
            var stream = port.Client.GetStream();
            var header = new byte[4];
            try
            {
                while (await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length is < 14 or > MaxFrame)
                        break;   // not a QEMU stream: drop the connection rather than misread what follows
                    var frame = new byte[length];
                    if (!await ReadExactlyAsync(stream, frame, cancellationToken).ConfigureAwait(false))
                        break;
                    await ForwardAsync(port, frame, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The machine stopped, or the switch is shutting down.
            }
            finally
            {
                Disconnect(port);
            }
        }

        /// <summary>Delivers one frame that came in on <paramref name="from"/>.</summary>
        private async Task ForwardAsync(SwitchPort from, byte[] frame, CancellationToken cancellationToken)
        {
            var destination = new PhysicalAddress(frame[..6]);
            var source = new PhysicalAddress(frame[6..12]);
            var broadcast = (frame[0] & 1) != 0;   // group bit: broadcast and multicast

            List<SwitchPort> targets;
            lock (_lock)
            {
                if ((frame[6] & 1) == 0)
                    _macTable[source] = from;

                targets = !broadcast && _macTable.TryGetValue(destination, out var known)
                    ? known == from ? new List<SwitchPort>() : new List<SwitchPort> { known }
                    : _ports.Where(p => p != from).ToList();
            }

            foreach (var target in targets)
                await target.SendAsync(frame, cancellationToken).ConfigureAwait(false);

            // The DHCP server sits on the switch itself: it hears broadcasts and what is sent to its MAC.
            if (_dhcp is not null && (broadcast || destination.GetAddressBytes().AsSpan().SequenceEqual(DhcpResponder.Mac))
                && _dhcp.Answer(frame) is { } reply)
            {
                await from.SendAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }

        private void Disconnect(SwitchPort port)
        {
            lock (_lock)
            {
                _ports.Remove(port);
                foreach (var stale in _macTable.Where(entry => entry.Value == port).Select(entry => entry.Key).ToList())
                    _macTable.Remove(stale);
            }
            port.Client.Dispose();
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    return false;
                read += count;
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            if (_accepting is not null)
                await _accepting.ConfigureAwait(false);
            lock (_lock)
            {
                foreach (var port in _ports)
                    port.Client.Dispose();
                _ports.Clear();
            }
            _stop.Dispose();
        }

        private sealed class SwitchPort
        {
            private readonly SemaphoreSlim _writeLock = new(1, 1);

            public SwitchPort(TcpClient client) => Client = client;

            public TcpClient Client { get; }

            /// <summary>One frame, length first; serialised so frames from several senders never interleave.</summary>
            public async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
            {
                var message = new byte[4 + frame.Length];
                BinaryPrimitives.WriteInt32BigEndian(message, frame.Length);
                frame.CopyTo(message, 4);

                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await Client.GetStream().WriteAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // That machine is going away; its reader removes the port.
                }
                finally
                {
                    _writeLock.Release();
                }
            }
        }
    }
}
