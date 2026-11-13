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

        /// <summary>
        /// The most addresses the switch learns: they come from what guests send, and a guest sending from random MACs
        /// grew the table until WinQEMU ran out of memory. Beyond it, unknown senders are flooded, as unknown
        /// destinations are.
        /// </summary>
        private const int MaxLearned = 4096;

        private readonly TcpListener _listener = new(Qemu.PrivateLoopback.Address, 0);
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

        /// <summary>The machines' cards connected now (not this PC's TAP adapter nor the physical card).</summary>
        public int MachinePorts
        {
            get { lock (_lock) return _ports.Count(p => p is StreamPort); }
        }

        private bool _hadUplink;

        /// <summary>Whether the TAP adapter or physical card the switch was given has gone (disabled, removed).</summary>
        public bool LostUplink
        {
            get { lock (_lock) return _hadUplink && !_ports.Any(p => p is not StreamPort); }
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
                var port = new StreamPort(client);
                lock (_lock)
                    _ports.Add(port);
                port.StartWriting(cancellationToken);
                _ = ServeAsync(port, cancellationToken);
            }
        }

        /// <summary>
        /// Plugs this PC into the network through a TAP-Windows adapter: what Windows sends on it is
        /// switched like a machine's frames, and what is switched to it, Windows receives.
        /// </summary>
        public void Attach(TapDevice device)
        {
            var port = new TapPort(device);
            lock (_lock)
            {
                _ports.Add(port);
                _hadUplink = true;
            }
            port.StartWriting(_stop.Token);
            _ = ServeTapAsync(port, _stop.Token);
        }

        private async Task ServeTapAsync(TapPort port, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await port.Device.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (frame.Length >= 14)
                        Forward(port, frame);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The switch is shutting down, or the adapter was disabled or removed.
            }
            finally
            {
                Disconnect(port);
            }
        }

        /// <summary>
        /// Plugs the network into a physical card through Npcap: the machines' frames go out under this
        /// PC's MAC, and what comes back for their addresses is switched to them (MacTranslator).
        /// </summary>
        public void Attach(PcapDevice device, MacTranslator translator)
        {
            var port = new PcapPort(device, translator);
            lock (_lock)
            {
                _ports.Add(port);
                _hadUplink = true;
            }
            port.StartWriting(_stop.Token);
            // pcap_next_ex blocks for up to its timeout: a thread of its own rather than a pool thread.
            _ = Task.Factory.StartNew(() => ServePcap(port, _stop.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void ServePcap(PcapPort port, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (port.Device.Read() is { } seen && port.Translator.Incoming(seen) is { } frame)
                        Forward(port, frame);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The switch is shutting down, or the card went away (disabled, Wi-Fi switched off).
            }
            finally
            {
                Disconnect(port);
            }
        }

        private async Task ServeAsync(StreamPort port, CancellationToken cancellationToken)
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
                    Forward(port, frame);
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

        /// <summary>
        /// Delivers one frame that came in on <paramref name="from"/>: queued on each target port, never waited for. A
        /// port that stopped reading (a paused machine, a guest whose driver takes nothing) filled its socket, and the
        /// sender then waited on it, the next sender on that one, until no machine on the network passed any traffic.
        /// </summary>
        private void Forward(SwitchPort from, byte[] frame)
        {
            var destination = new PhysicalAddress(frame[..6]);
            var source = new PhysicalAddress(frame[6..12]);
            var broadcast = (frame[0] & 1) != 0;   // group bit: broadcast and multicast

            List<SwitchPort> targets;
            lock (_lock)
            {
                if ((frame[6] & 1) == 0 && (_macTable.Count < MaxLearned || _macTable.ContainsKey(source)))
                    _macTable[source] = from;

                targets = !broadcast && _macTable.TryGetValue(destination, out var known)
                    ? known == from ? new List<SwitchPort>() : new List<SwitchPort> { known }
                    : _ports.Where(p => p != from).ToList();
            }

            foreach (var target in targets)
                target.Queue(frame);

            // The DHCP server sits on the switch itself: it hears broadcasts and what is sent to its MAC. A failure
            // there is the server's, not the sender's: it escaped the loop, which then disconnected the sender.
            if (_dhcp is not null && (broadcast || destination.GetAddressBytes().AsSpan().SequenceEqual(DhcpResponder.Mac)))
            {
                byte[]? reply = null;
                try
                {
                    reply = _dhcp.Answer(frame);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                {
                }
                if (reply is not null)
                    from.Queue(reply);
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
            port.Close();
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
            List<SwitchPort> ports;
            lock (_lock)
            {
                ports = _ports.ToList();
                _ports.Clear();
            }
            foreach (var port in ports)
                port.Close();
            _stop.Dispose();
        }

        /// <summary>
        /// A port of the switch: something frames are delivered to, through a queue of its own written by one task, so
        /// frames from several senders never interleave and no sender waits on it. A full queue drops the frame, as a
        /// switch with a congested port does.
        /// </summary>
        private abstract class SwitchPort
        {
            private readonly System.Threading.Channels.Channel<byte[]> _queue =
                System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(256)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
                    SingleReader = true
                });

            public void Queue(byte[] frame) => _queue.Writer.TryWrite(frame);

            public void StartWriting(CancellationToken cancellationToken) => _ = WriteAllAsync(cancellationToken);

            private async Task WriteAllAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await foreach (var frame in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // That port is going away; its reader removes it.
                }
            }

            protected abstract Task WriteAsync(byte[] frame, CancellationToken cancellationToken);

            public void Close()
            {
                _queue.Writer.TryComplete();
                Release();
            }

            protected abstract void Release();
        }

        /// <summary>A machine's card: QEMU's stream netdev, each frame preceded by its length.</summary>
        private sealed class StreamPort : SwitchPort
        {
            public StreamPort(TcpClient client) => Client = client;

            public TcpClient Client { get; }

            protected override async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
            {
                var message = new byte[4 + frame.Length];
                BinaryPrimitives.WriteInt32BigEndian(message, frame.Length);
                frame.CopyTo(message, 4);
                await Client.GetStream().WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }

            protected override void Release() => Client.Dispose();
        }

        /// <summary>A physical card, through Npcap and the address translation it needs.</summary>
        private sealed class PcapPort : SwitchPort
        {
            public PcapPort(PcapDevice device, MacTranslator translator)
            {
                Device = device;
                Translator = translator;
            }

            public PcapDevice Device { get; }

            public MacTranslator Translator { get; }

            protected override Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
            {
                if (Translator.Outgoing(frame) is { } translated)
                    Device.Send(translated);
                return Task.CompletedTask;
            }

            protected override void Release() => Device.Dispose();
        }

        /// <summary>This PC, through a TAP-Windows adapter: one frame per write, no framing.</summary>
        private sealed class TapPort : SwitchPort
        {
            public TapPort(TapDevice device) => Device = device;

            public TapDevice Device { get; }

            protected override Task WriteAsync(byte[] frame, CancellationToken cancellationToken) =>
                Device.WriteAsync(frame, cancellationToken);

            protected override void Release() => _ = Device.DisposeAsync();
        }
    }
}
