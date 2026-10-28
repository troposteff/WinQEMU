using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WinQEMU.Core.Cli;

namespace WinQEMU.Cli
{
    /// <summary>
    /// Serves the winqemu command (WinQEMU.Cli): each connection brings one request, answered by the handler on
    /// the interface's thread, which alone may touch the machines' data-bound configuration.
    /// </summary>
    public sealed class CommandServer : IDisposable
    {
        private readonly Func<CliRequest, CancellationToken, Task<CliResponse>> _handler;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
        private readonly CancellationTokenSource _stop = new();

        public CommandServer(Func<CliRequest, CancellationToken, Task<CliResponse>> handler, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            _handler = handler;
            _dispatcher = dispatcher;
        }

        public void Start() => _ = Task.Run(() => AcceptAsync(_stop.Token));

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // Another WinQEMU of this user holds the name (it cannot: one instance per user), or the
                    // pipe broke while waiting; try again rather than leave the command line without a server.
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _ = Task.Run(() => ServeAsync(pipe, cancellationToken));
            }
        }

        /// <summary>This user only: the pipe would otherwise let anyone on the PC start, stop or snapshot their machines.</summary>
        private static NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(CliProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            await using (pipe)
            {
                try
                {
                    var request = await CliProtocol.ReadAsync<CliRequest>(pipe, cancellationToken).ConfigureAwait(false);
                    if (request is null)
                        return;
                    var response = await RunOnInterfaceThreadAsync(request, cancellationToken).ConfigureAwait(false);
                    await CliProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
                {
                    // The terminal went away (Ctrl+C) or sent nonsense: nothing to answer.
                }
            }
        }

        private Task<CliResponse> RunOnInterfaceThreadAsync(CliRequest request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<CliResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.SetResult(await _handler(request, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        completion.SetResult(new CliResponse(CliProtocol.Failure, string.Empty, ex.Message));
                    }
                }))
                completion.SetResult(new CliResponse(CliProtocol.Failure, string.Empty, Core.Text.CliText.Closing));
            return completion.Task;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
