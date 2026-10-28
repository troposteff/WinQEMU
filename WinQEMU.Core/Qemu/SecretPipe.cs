using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// Hands a password to one QEMU process through a named pipe, read once as the file of a secret object
    /// (<c>--object secret,id=…,file=\\.\pipe\winqemu-…</c>), so it never appears on a command line, which any local
    /// process can read, nor on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verified with qemu-img, qemu-io and qemu-system-x86_64 11.1: the secret object reads the pipe like a file
    /// (GLib's g_file_get_contents) until end of file. End of file must come from closing the server's handle: after
    /// DisconnectNamedPipe the read failed with "Error reading file …: Invalid argument". So: write, wait for the
    /// client to have read it all, close.
    /// </para>
    /// <para>
    /// Only the current user may open the pipe (its DACL), it is ours from the start (FirstPipeInstance: nobody
    /// created that name before us), it has a single instance, a random ASCII name (QEMU opens files through the ANSI
    /// API), and the password is written only to the process WinQEMU started (GetNamedPipeClientProcessId). Any other
    /// client gets nothing, and the QEMU it got ahead of fails to read its secret rather than reading a wrong one.
    /// </para>
    /// </remarks>
    public sealed class SecretPipe : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly byte[] _secret;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<int> _client = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serving;

        private SecretPipe(byte[] secret)
        {
            _secret = secret;
            var name = "winqemu-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Path = @"\\.\pipe\" + name;

            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID for the current process.");
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

            _server = NamedPipeServerStreamAcl.Create(
                name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
            _serving = ServeAsync();
        }

        /// <summary>The file name to give QEMU: <c>\\.\pipe\winqemu-&lt;32 hex&gt;</c>.</summary>
        public string Path { get; }

        /// <summary>Opens the pipe; it serves <paramref name="secret"/> once, to the process named by <see cref="ExpectClient"/>.</summary>
        public static SecretPipe Serve(byte[] secret) => new(secret);

        /// <summary>The process allowed to read the secret, known once it has been started.</summary>
        public void ExpectClient(int processId) => _client.TrySetResult(processId);

        /// <summary>Completes once the secret has been read, or the pipe was closed without serving it.</summary>
        public Task Served => _serving;

        private async Task ServeAsync()
        {
            try
            {
                await _server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var expected = await _client.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(_server.SafePipeHandle, out var actual) || actual != (uint)expected)
                    return;

                await _server.WriteAsync(_secret, _stop.Token).ConfigureAwait(false);
                await _server.FlushAsync(_stop.Token).ConfigureAwait(false);
                // Closed only once QEMU has read everything: the close is its end of file.
                await Task.Run(_server.WaitForPipeDrain).WaitAsync(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
            {
                // The process ended without reading, or read and left: nothing more to serve either way.
            }
            finally
            {
                Array.Clear(_secret);
                _server.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _serving.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            _stop.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    }
}
