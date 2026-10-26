using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinQEMU.Core.Qemu
{
    public sealed class QmpEventArgs : EventArgs
    {
        public required string Name { get; init; }
        public JsonNode? Data { get; init; }
    }

    public sealed class QmpException : Exception
    {
        public QmpException(string message, string? errorClass = null) : base(message)
        {
            ErrorClass = errorClass;
        }

        public string? ErrorClass { get; }
    }

    /// <summary>
    /// Async QMP (QEMU Machine Protocol) client over loopback TCP. QMP is what makes real VM
    /// management possible — pause/resume, snapshots, hot-plug and live statistics all go through
    /// here rather than through the command line.
    /// </summary>
    public sealed class QmpClient : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();
        private Task? _readLoop;
        private long _nextCommandId;
        private volatile bool _disposed;

        private QmpClient(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            // QMP is newline-delimited UTF-8 JSON in both directions.
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        }

        /// <summary>Raised for asynchronous QEMU events such as SHUTDOWN, STOP, RESUME and RESET.</summary>
        public event EventHandler<QmpEventArgs>? EventReceived;

        /// <summary>Raised once when the connection drops, whether by guest shutdown or by failure.</summary>
        public event EventHandler? Disconnected;

        public bool IsConnected => !_disposed && _client.Connected;

        /// <summary>
        /// Connects and completes the capability handshake. QEMU opens its listening socket during
        /// startup, so connection attempts are retried until <paramref name="timeout"/> elapses.
        /// </summary>
        /// <param name="trace">
        /// Optional progress sink. Connecting spans several distinct steps that can each stall, so the
        /// caller needs to be able to log which one it got to.
        /// </param>
        public static async Task<QmpClient> ConnectAsync(
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Action<string>? trace = null)
        {
            var deadline = DateTime.UtcNow + timeout;
            Exception? lastFailure = null;
            var attempts = 0;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;

                var tcp = new TcpClient();
                try
                {
                    // The deadline has to cover the handshake too. Bounding only the TCP connect
                    // leaves a silent peer able to hang the caller forever.
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;

                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(remaining);

                    await tcp.ConnectAsync("127.0.0.1", port, attempt.Token).ConfigureAwait(false);
                    trace?.Invoke($"QMP: TCP connected on port {port.ToString(CultureInfo.InvariantCulture)}.");

                    var client = new QmpClient(tcp);
                    await client.HandshakeAsync(attempt.Token, trace).ConfigureAwait(false);
                    return client;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    tcp.Dispose();
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // The per-attempt deadline expired, which means the overall timeout is up.
                    tcp.Dispose();
                    lastFailure = new QmpException("the handshake did not complete before the timeout");
                    break;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    tcp.Dispose();
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new QmpException(
                $"Could not establish a QMP connection on port {port.ToString(CultureInfo.InvariantCulture)} " +
                $"within {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s " +
                $"after {attempts.ToString(CultureInfo.InvariantCulture)} attempt(s). " +
                $"{lastFailure?.Message}".Trim());
        }

        private async Task HandshakeAsync(CancellationToken cancellationToken, Action<string>? trace)
        {
            // QEMU greets with {"QMP": {...}} and refuses every command until capabilities are negotiated.
            var greeting = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (greeting is null || !greeting.Contains("\"QMP\"", StringComparison.Ordinal))
                throw new QmpException("The peer did not send a QMP greeting; this is not a QMP socket.");

            trace?.Invoke("QMP: greeting received.");

            // Start the dispatcher only now, so the greeting is not consumed by the loop.
            _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));

            await ExecuteAsync("qmp_capabilities", null, cancellationToken).ConfigureAwait(false);
            trace?.Invoke("QMP: capabilities negotiated.");
        }

        /// <summary>Executes a QMP command and returns its <c>return</c> payload.</summary>
        public async Task<JsonNode?> ExecuteAsync(
            string command,
            object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var id = Interlocked.Increment(ref _nextCommandId).ToString(CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            var request = new JsonObject
            {
                ["execute"] = command,
                ["id"] = id
            };
            if (arguments is not null)
                request["arguments"] = JsonSerializer.SerializeToNode(arguments);

            try
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
                    await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                using var registration = cancellationToken.Register(
                    () => completion.TrySetCanceled(cancellationToken));
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Runs a human-monitor (HMP) command. Needed for the handful of operations QEMU still only
        /// exposes there — internal snapshots via <c>savevm</c>/<c>loadvm</c> being the important ones.
        /// </summary>
        public async Task<string> ExecuteHumanCommandAsync(
            string commandLine,
            CancellationToken cancellationToken = default)
        {
            // QMP argument names are hyphenated ("command-line"), which no C# identifier can express,
            // so the payload is built as a JSON object rather than an anonymous type.
            var result = await ExecuteAsync(
                "human-monitor-command",
                new JsonObject { ["command-line"] = commandLine },
                cancellationToken).ConfigureAwait(false);

            return result?.GetValue<string>() ?? string.Empty;
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break;

                    if (line.Length == 0)
                        continue;

                    JsonNode? message;
                    try
                    {
                        message = JsonNode.Parse(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (message is not JsonObject envelope)
                        continue;

                    if (envelope.TryGetPropertyValue("event", out var eventName) && eventName is not null)
                    {
                        envelope.TryGetPropertyValue("data", out var data);
                        EventReceived?.Invoke(this, new QmpEventArgs
                        {
                            Name = eventName.GetValue<string>(),
                            Data = data?.DeepClone()
                        });
                        continue;
                    }

                    if (!envelope.TryGetPropertyValue("id", out var idNode) || idNode is null)
                        continue;

                    var id = idNode.GetValue<string>();
                    if (!_pending.TryRemove(id, out var completion))
                        continue;

                    if (envelope.TryGetPropertyValue("error", out var error) && error is JsonObject errorObject)
                    {
                        var description = errorObject["desc"]?.GetValue<string>() ?? "The QMP command failed.";
                        var errorClass = errorObject["class"]?.GetValue<string>();
                        completion.TrySetException(new QmpException(description, errorClass));
                    }
                    else
                    {
                        envelope.TryGetPropertyValue("return", out var payload);
                        completion.TrySetResult(payload?.DeepClone());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                FailPending(ex);
            }
            finally
            {
                FailPending(new QmpException("The QMP connection was closed."));
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private void FailPending(Exception exception)
        {
            foreach (var key in _pending.Keys.ToArray())
            {
                if (_pending.TryRemove(key, out var completion))
                    completion.TrySetException(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            await _shutdown.CancelAsync().ConfigureAwait(false);

            try
            {
                if (_readLoop is not null)
                    await _readLoop.ConfigureAwait(false);
            }
            catch
            {
                // The loop is being torn down; its failure is not interesting here.
            }

            _reader.Dispose();
            _writer.Dispose();
            _client.Dispose();
            _writeLock.Dispose();
            _shutdown.Dispose();
        }
    }
}
