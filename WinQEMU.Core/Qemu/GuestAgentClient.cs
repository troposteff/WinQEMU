using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WinQEMU.Core.Qemu
{
    public sealed class GuestAgentException : Exception
    {
        public GuestAgentException(string message) : base(message)
        {
        }
    }

    /// <summary>The outcome of a program run in the guest with guest-exec.</summary>
    public sealed record GuestExecResult(int ExitCode, string Output, string Error);

    /// <summary>
    /// Talks to qemu-guest-agent inside the guest, through the virtio-serial port QEMU exposes on a
    /// loopback socket (see <see cref="QemuArgumentBuilder"/>).
    /// </summary>
    /// <remarks>
    /// Not QMP: there is no greeting and nothing tells whether an agent is listening at all. Commands
    /// written while none runs wait in the port and are answered, out of turn, by whichever agent starts
    /// later. Hence guest-sync-delimited before anything else: the agent echoes a fresh id after a 0xFF
    /// byte, and every line before it is stale.
    /// </remarks>
    public sealed class GuestAgentClient : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private GuestAgentClient(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public static async Task<GuestAgentClient> ConnectAsync(int port, CancellationToken cancellationToken = default)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
                return new GuestAgentClient(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// True once an agent has answered. False after <paramref name="timeout"/> without one: no agent
        /// installed, or the guest not booted yet. The connection is unusable afterwards either way it fails.
        /// </summary>
        public async Task<bool> SynchronizeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var id = Random.Shared.Next(1, int.MaxValue);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(
                    $"{{\"execute\":\"guest-sync-delimited\",\"arguments\":{{\"id\":{id.ToString(CultureInfo.InvariantCulture)}}}}}")
                    .ConfigureAwait(false);

                while (true)
                {
                    var line = await _reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                    if (line is null)
                        return false;

                    if (ParseLine(line)?["return"] is JsonValue value && value.TryGetValue<long>(out var echoed) && echoed == id)
                        return true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Runs an agent command and returns its "return" payload.</summary>
        public async Task<JsonNode?> ExecuteAsync(string command, JsonObject? arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var request = new JsonObject { ["execute"] = command };
            if (arguments is not null)
                request["arguments"] = arguments;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
                while (true)
                {
                    var line = await _reader.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                               ?? throw new GuestAgentException("The guest agent closed the connection.");
                    var reply = ParseLine(line);
                    if (reply is null)
                        continue;
                    if (reply["error"] is JsonObject error)
                        throw new GuestAgentException(error["desc"]?.GetValue<string>() ?? "The guest agent reported an error.");
                    return reply["return"];
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GuestAgentException($"The guest agent did not answer {command} in time.");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Runs a program in the guest (as root on Linux) and waits for it. <paramref name="input"/> goes to
        /// its standard input, which is how secrets are passed: never on a command line, readable in ps.
        /// </summary>
        public async Task<GuestExecResult> RunAsync(string path, IReadOnlyList<string> arguments, string? input,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var request = new JsonObject
            {
                ["path"] = path,
                ["arg"] = new JsonArray(arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["capture-output"] = true
            };
            if (input is not null)
                request["input-data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

            // Starting a program can take a while in a guest that is still booting (Windows took more than 10 s).
            var started = await ExecuteAsync("guest-exec", request, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var pid = started?["pid"]?.GetValue<long>() ?? throw new GuestAgentException("guest-exec returned no pid.");

            var end = DateTime.UtcNow + timeout;
            while (true)
            {
                var status = await ExecuteAsync(
                    "guest-exec-status", new JsonObject { ["pid"] = pid }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (status?["exited"]?.GetValue<bool>() == true)
                {
                    return new GuestExecResult(
                        status["exitcode"]?.GetValue<int>() ?? -1,
                        Decode(status["out-data"]),
                        Decode(status["err-data"]));
                }

                if (DateTime.UtcNow > end)
                    throw new GuestAgentException($"{path} was still running in the guest after {timeout.TotalSeconds:0} s.");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string Decode(JsonNode? base64) =>
            base64?.GetValue<string>() is { Length: > 0 } text ? Encoding.UTF8.GetString(Convert.FromBase64String(text)) : string.Empty;

        /// <summary>A reply line, without the 0xFF guest-sync-delimited puts before its answer.</summary>
        private static JsonObject? ParseLine(string line)
        {
            var json = line.TrimStart('ÿ', '�').Trim();
            if (json.Length == 0)
                return null;
            try
            {
                return JsonNode.Parse(json) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _lock.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
