using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;

namespace WinQEMU.Core.Cli
{
    /// <summary>A command typed in a terminal ("winqemu start Debian"), as the command line hands it to WinQEMU.</summary>
    /// <param name="Password">An encrypted machine's password, read by the command line from its standard input.</param>
    public sealed record CliRequest(string Command, string[] Arguments, string? Password = null);

    /// <summary>What WinQEMU answers: the text to print, and the command line's exit code (0 success, 1 failure, 2 misuse).</summary>
    public sealed record CliResponse(int ExitCode, string Output, string? Error = null);

    /// <summary>
    /// The channel between the winqemu command and the running WinQEMU: a named pipe of this Windows user,
    /// each message a 4-byte big-endian length then UTF-8 JSON.
    /// </summary>
    /// <remarks>
    /// The command drives WinQEMU rather than QEMU (the user's choice): a machine started from a terminal is the
    /// one the window shows, with its console, and nothing runs twice. The pipe's name carries the user's SID and
    /// its access is limited to that user (CommandServer), so another session cannot drive this one's machines.
    /// </remarks>
    public static class CliProtocol
    {
        private const int MaxMessageBytes = 16 * 1024 * 1024;

        public const int Success = 0;
        public const int Failure = 1;
        public const int Misuse = 2;

        /// <summary>An encrypted machine needs its password: the command line asks for it when a person is typing.</summary>
        public const int PasswordRequired = 10;

        public static string PipeName => "WinQEMU.cli." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

        public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The next message, or default when the other side closed the pipe first.</summary>
        public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
        {
            var header = new byte[4];
            if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
                return default;
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is < 0 or > MaxMessageBytes)
                throw new InvalidDataException($"A command line message of {length} bytes.");
            var body = new byte[length];
            if (!await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false))
                return default;
            return JsonSerializer.Deserialize<T>(body);
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var done = 0;
            while (done < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(done), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return false;
                done += read;
            }
            return true;
        }
    }
}
