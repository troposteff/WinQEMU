using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// A VNC client that does nothing but share the clipboard with QEMU.
    /// </summary>
    /// <remarks>
    /// QEMU's SDL display has no clipboard support at all, and the only clipboard peer its Windows build
    /// offers on the host side is the VNC server. The guest side is the qemu-vdagent chardev, which talks
    /// to spice-vdagent inside the guest; QEMU relays between the two. This client never asks for a
    /// framebuffer — the display stays the embedded SDL window — it only speaks the RFB "Extended
    /// Clipboard" pseudo-encoding, which carries UTF-8 text, unlike the legacy Latin-1 cut-text messages.
    ///
    /// Host to guest: <see cref="SetHostText"/> announces new text (Notify); QEMU asks for it (Request)
    /// when the guest pastes, and gets it (Provide). Guest to host: QEMU announces (Notify), this client
    /// asks straight away and raises <see cref="GuestTextReceived"/> when the text arrives.
    /// </remarks>
    public sealed class VncClipboardClient : IAsyncDisposable
    {
        private const int ExtendedClipboardEncoding = unchecked((int)0xC0A1E5CE);

        private const uint FormatText = 1u << 0;
        private const uint ActionCaps = 1u << 24;
        private const uint ActionRequest = 1u << 25;
        private const uint ActionPeek = 1u << 26;
        private const uint ActionNotify = 1u << 27;
        private const uint ActionProvide = 1u << 28;

        /// <summary>Largest text accepted or sent, matching what QEMU advertises by default.</summary>
        private const int MaxTextBytes = 10 * 1024 * 1024;

        private readonly TcpClient _tcp = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly string _guestLineEnding;
        private NetworkStream? _stream;
        private Task? _readLoop;
        private string? _hostText;

        /// <param name="guestLineEnding">"\n" for Linux guests, "\r\n" for Windows ones.</param>
        public VncClipboardClient(string guestLineEnding = "\n")
        {
            _guestLineEnding = guestLineEnding;
        }

        /// <summary>Text copied inside the guest, already converted to Windows line endings.</summary>
        public event EventHandler<string>? GuestTextReceived;

        /// <summary>Raised once when the connection ends, with the reason if it failed.</summary>
        public event EventHandler<Exception?>? Disconnected;

        public bool IsConnected => _readLoop is { IsCompleted: false };

        public async Task ConnectAsync(int port, string password, CancellationToken cancellationToken = default)
        {
            await _tcp.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();

            await HandshakeAsync(_stream, password, cancellationToken).ConfigureAwait(false);

            // SetEncodings with the clipboard pseudo-encoding only. No real encoding is offered and no
            // FramebufferUpdateRequest is ever sent, so QEMU never sends pixels to this connection.
            var setEncodings = new byte[8];
            setEncodings[0] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(setEncodings.AsSpan(2), 1);
            BinaryPrimitives.WriteInt32BigEndian(setEncodings.AsSpan(4), ExtendedClipboardEncoding);
            await WriteAsync(setEncodings, cancellationToken).ConfigureAwait(false);

            // Our own capabilities: text, in every direction.
            await SendExtendedAsync(ActionCaps | ActionRequest | ActionPeek | ActionNotify | ActionProvide | FormatText,
                BigEndian(MaxTextBytes), cancellationToken).ConfigureAwait(false);

            _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
        }

        /// <summary>
        /// Makes <paramref name="text"/> the guest's clipboard. QEMU only fetches the text once the guest
        /// actually pastes, so this is cheap to call on every host clipboard change.
        /// </summary>
        public Task SetHostTextAsync(string text, CancellationToken cancellationToken = default)
        {
            _hostText = text;
            return SendExtendedAsync(ActionNotify | FormatText, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }

        private static async Task HandshakeAsync(NetworkStream stream, string password, CancellationToken cancellationToken)
        {
            var version = await ReadExactAsync(stream, 12, cancellationToken).ConfigureAwait(false);
            if (!Encoding.ASCII.GetString(version).StartsWith("RFB ", StringComparison.Ordinal))
                throw new IOException("Not an RFB server.");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), cancellationToken).ConfigureAwait(false);

            var count = (await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false))[0];
            if (count == 0)
                throw new IOException("VNC server refused the connection: " + await ReadReasonAsync(stream, cancellationToken).ConfigureAwait(false));
            var types = await ReadExactAsync(stream, count, cancellationToken).ConfigureAwait(false);
            if (!types.Contains((byte)2))
                throw new IOException("VNC server does not offer password authentication.");

            await stream.WriteAsync(new byte[] { 2 }, cancellationToken).ConfigureAwait(false);
            var challenge = await ReadExactAsync(stream, 16, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(EncryptChallenge(challenge, password), cancellationToken).ConfigureAwait(false);

            var result = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false));
            if (result != 0)
                throw new IOException("VNC authentication failed: " + await ReadReasonAsync(stream, cancellationToken).ConfigureAwait(false));

            // ClientInit, shared: never take the server away from anyone else.
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);

            // ServerInit: size, pixel format, then the desktop name — none of it is needed.
            var init = await ReadExactAsync(stream, 24, cancellationToken).ConfigureAwait(false);
            var nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
            await ReadExactAsync(stream, checked((int)nameLength), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// VNC authentication: DES-encrypt the challenge with the password, each key byte bit-reversed
        /// — a quirk of the original implementation that every server and client has kept since.
        /// </summary>
        internal static byte[] EncryptChallenge(byte[] challenge, string password)
        {
            var key = new byte[8];
            var raw = Encoding.ASCII.GetBytes(password);
            for (var i = 0; i < 8 && i < raw.Length; i++)
            {
                var b = raw[i];
                byte reversed = 0;
                for (var bit = 0; bit < 8; bit++)
                    reversed |= (byte)(((b >> bit) & 1) << (7 - bit));
                key[i] = reversed;
            }

            using var des = DES.Create();
            des.Key = key;
            return des.EncryptEcb(challenge, PaddingMode.None);
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            Exception? failure = null;
            try
            {
                var stream = _stream!;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var type = (await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false))[0];
                    switch (type)
                    {
                        case 2: // Bell
                            break;

                        case 3: // ServerCutText
                            var header = await ReadExactAsync(stream, 7, cancellationToken).ConfigureAwait(false);
                            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(3));
                            if (length >= 0)
                            {
                                // Legacy Latin-1 cut text; the extended messages carry the same content.
                                await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                var size = checked(-length);
                                if (size < 4 || size > MaxTextBytes + 1024)
                                    throw new IOException($"Unexpected extended clipboard message of {size} bytes.");
                                var payload = await ReadExactAsync(stream, size, cancellationToken).ConfigureAwait(false);
                                await HandleExtendedAsync(payload, cancellationToken).ConfigureAwait(false);
                            }
                            break;

                        default:
                            // Pixels are never requested, so anything else means the stream is out of step.
                            throw new IOException($"Unexpected VNC server message type {type}.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Disconnected?.Invoke(this, failure);
        }

        private async Task HandleExtendedAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var flags = BinaryPrimitives.ReadUInt32BigEndian(payload);

            // The server's capabilities carry action bits too; they announce, they do not ask.
            if ((flags & ActionCaps) != 0 || (flags & FormatText) == 0)
                return;

            if ((flags & ActionNotify) != 0)
            {
                // The guest copied something. Fetch it now rather than on the next Windows paste: the
                // shell has no hook that runs when another application pastes.
                await SendExtendedAsync(ActionRequest | FormatText, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }
            else if ((flags & ActionRequest) != 0)
            {
                if (_hostText is not null)
                    await SendExtendedAsync(ActionProvide | FormatText, Compress(EncodeText(_hostText)), cancellationToken).ConfigureAwait(false);
            }
            else if ((flags & ActionProvide) != 0)
            {
                var text = DecodeText(Decompress(payload.AsSpan(4)));
                if (text is not null)
                    GuestTextReceived?.Invoke(this, text);
            }
        }

        /// <summary>Text as QEMU expects it in a Provide: big-endian length, then UTF-8 with no terminator.</summary>
        private byte[] EncodeText(string text)
        {
            var normalized = text.Replace("\r\n", "\n").Replace("\n", _guestLineEnding);
            var bytes = Encoding.UTF8.GetBytes(normalized);
            if (bytes.Length > MaxTextBytes)
                bytes = bytes.AsSpan(0, MaxTextBytes).ToArray();
            var block = new byte[4 + bytes.Length];
            BinaryPrimitives.WriteUInt32BigEndian(block, (uint)bytes.Length);
            bytes.CopyTo(block, 4);
            return block;
        }

        private static string? DecodeText(byte[] block)
        {
            if (block.Length < 4)
                return null;
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(block), (uint)(block.Length - 4));
            var text = Encoding.UTF8.GetString(block, 4, length).TrimEnd('\0');
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(data);
            return output.ToArray();
        }

        private static byte[] Decompress(ReadOnlySpan<byte> data)
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxTextBytes + 4)
                    throw new IOException("Clipboard data exceeds the size limit.");
            }
            return output.ToArray();
        }

        private async Task SendExtendedAsync(uint flags, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            // ClientCutText with a negative length marks the extended format.
            var message = new byte[8 + 4 + data.Length];
            message[0] = 6;
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), -(4 + data.Length));
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), flags);
            data.Span.CopyTo(message.AsSpan(12));
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteAsync(byte[] message, CancellationToken cancellationToken)
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected.");
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static byte[] BigEndian(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        private static async Task<string> ReadReasonAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            try
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false));
                return Encoding.UTF8.GetString(await ReadExactAsync(stream, (int)Math.Min(length, 4096), cancellationToken).ConfigureAwait(false));
            }
            catch (IOException)
            {
                return "(no reason given)";
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _tcp.Dispose();
            if (_readLoop is not null)
            {
                try { await _readLoop.ConfigureAwait(false); } catch { }
            }
            _shutdown.Dispose();
            _writeLock.Dispose();
        }
    }
}
