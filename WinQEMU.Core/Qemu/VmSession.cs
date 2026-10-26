using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// Owns one running VM: the QEMU process, its QMP connection and its observable state.
    /// </summary>
    /// <remarks>
    /// Events are raised on background threads (the process watcher and the QMP read loop).
    /// UI consumers must marshal onto the dispatcher themselves.
    /// </remarks>
    public sealed class VmSession : ObservableBase, IAsyncDisposable
    {
        private const int MaxLogLines = 2000;

        private readonly QemuInstallation _installation;
        private readonly List<string> _log = new();
        private readonly SemaphoreSlim _transitionLock = new(1, 1);

        private Process? _process;
        private QmpClient? _qmp;
        private TaskCompletionSource? _processExited;
        private string? _lastQemuMessage;
        private TimeSpan _lastCpuTime;
        private DateTime _lastCpuSampleUtc;
        private bool _disposed;

        /// <summary>Set when QEMU shut down because of a reboot, so the exit handler starts it again.</summary>
        private volatile bool _restartAfterExit;

        private VncClipboardClient? _clipboard;

        public VmSession(VmConfig config, QemuInstallation installation)
        {
            Config = config;
            _installation = installation;
        }

        public VmConfig Config { get; }

        private VmState _state = VmState.Stopped;
        public VmState State
        {
            get => _state;
            private set
            {
                if (SetProperty(ref _state, value))
                {
                    RaisePropertyChanged(nameof(IsRunning));
                    RaisePropertyChanged(nameof(CanStart));
                    StateChanged?.Invoke(this, value);
                }
            }
        }

        private string? _lastError;
        public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

        /// <summary>Process id of the running emulator, used by the shell to locate and reparent the SDL window.</summary>
        public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

        /// <summary>The exact command line that was launched. Shown in the log so failures are reproducible by hand.</summary>
        public string? LaunchCommandLine { get; private set; }

        public bool IsRunning => State is VmState.Running or VmState.Paused;
        public bool CanStart => State == VmState.Stopped;

        public event EventHandler<VmState>? StateChanged;

        /// <summary>Text the guest copied, with Windows line endings. Raised on a background thread.</summary>
        public event EventHandler<string>? GuestClipboardChanged;

        /// <summary>Raised once the clipboard channel is up, so the host's current text can be offered.</summary>
        public event EventHandler? ClipboardReady;
        public event EventHandler<string>? LogLineAppended;

        public IReadOnlyList<string> Log
        {
            get { lock (_log) return _log.ToArray(); }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State != VmState.Stopped)
                    throw new InvalidOperationException($"The VM is already {State}.");

                var problems = QemuArgumentBuilder.Validate(Config, _installation);
                if (problems.Count > 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, problems));

                LastError = null;
                State = VmState.Starting;

                EnsureUefiVarsStore();

                var qmpPort = ReserveLoopbackPort();
                int? clipboardPort = Config.EnableClipboard ? ReserveLoopbackPort() : null;
                GuestAgentPort = ReserveLoopbackPort();
                // The folders themselves come from the shell (UpdateSharedFolders), not from Config: this
                // runs off the UI thread, which alone may read the data-bound configuration.
                var sharesPort = ReserveLoopbackPort();
                var executable = _installation.GetSystemBinary(Config.Architecture);
                var arguments = QemuArgumentBuilder.Build(Config, _installation, qmpPort, clipboardPort, GuestAgentPort, sharesPort);

                LaunchCommandLine = QemuArgumentBuilder.ToDisplayString(executable, arguments);
                AppendLog($"Launching: {LaunchCommandLine}");

                var accelerator = QemuArgumentBuilder.ResolveAccelerator(Config, _installation);
                if (accelerator == AcceleratorType.Tcg)
                {
                    AppendLog("Warning: running without hardware acceleration (TCG). " +
                              "Expect roughly an order of magnitude less performance.");
                }

                StartProcess(executable, arguments);

                try
                {
                    await ConnectQmpAsync(qmpPort, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // No QMP means no control over the VM, so a half-managed process is worse than none.
                    AppendLog($"QMP handshake failed: {ex.Message}");
                    await KillProcessAsync().ConfigureAwait(false);
                    LastError = ex.Message;
                    State = VmState.Error;
                    throw;
                }

                AppendLog("QMP: client ready.");

                // Deliberately does not touch Config here. This continuation runs on the thread pool,
                // and VmConfig is data-bound in the shell, so raising PropertyChanged from here throws
                // RPC_E_WRONG_THREAD inside the binding engine. Bookkeeping such as LastStartedUtc is
                // the shell's job, after it has resumed on its own thread.
                State = VmState.Running;
                AppendLog("VM is running.");

                if (clipboardPort is { } port && port >= QemuArgumentBuilder.VncBasePort)
                    _ = StartClipboardAsync(port);

                _ = ServeSharedFoldersAsync(sharesPort);
            }
            catch (Exception ex)
            {
                // Without this, a failure between launching QEMU and reporting Running leaves the UI
                // stuck on "Starting" with nothing in the log to explain why.
                AppendLog($"Start failed: {ex.GetType().Name}: {ex.Message}");
                LastError = ex.Message;
                if (State != VmState.Stopped)
                    State = VmState.Error;
                throw;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <summary>Freezes guest execution (QMP <c>stop</c>). The guest is unaware and resumes exactly where it left off.</summary>
        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            await RequireQmp().ExecuteAsync("stop", null, cancellationToken).ConfigureAwait(false);
            State = VmState.Paused;
            AppendLog("VM paused.");
        }

        public async Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            await RequireQmp().ExecuteAsync("cont", null, cancellationToken).ConfigureAwait(false);
            State = VmState.Running;
            AppendLog("VM resumed.");
        }

        /// <summary>
        /// Presses the virtual power button (ACPI). The guest OS shuts down cleanly, or ignores it
        /// entirely if no ACPI-aware OS is running — this is a request, not a guarantee.
        /// </summary>
        public async Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            State = VmState.Stopping;
            AppendLog("ACPI power button pressed; waiting for the guest to shut down.");
            await RequireQmp().ExecuteAsync("system_powerdown", null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Hard reset, equivalent to the reset button. The guest loses unwritten data.</summary>
        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            AppendLog("Hard reset requested.");
            return RequireQmp().ExecuteAsync("system_reset", null, cancellationToken);
        }

        /// <summary>Cuts power immediately. Used for "Power off" and as the fallback when a graceful shutdown stalls.</summary>
        public async Task PowerOffAsync(CancellationToken cancellationToken = default)
        {
            await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == VmState.Stopped)
                    return;

                State = VmState.Stopping;
                AppendLog("Powering off.");

                var qmp = _qmp;
                if (qmp is { IsConnected: true })
                {
                    try
                    {
                        // 'quit' makes QEMU exit; the process watcher then drives the state to Stopped.
                        await qmp.ExecuteAsync("quit", null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"QMP quit failed ({ex.Message}); terminating the process instead.");
                    }
                }

                var process = _process;
                if (process is not null && !process.HasExited)
                {
                    try
                    {
                        // QEMU normally exits within a few hundred milliseconds of 'quit'.
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await KillProcessAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <summary>
        /// True when a snapshot can include guest RAM. WHPX cannot serialise vCPU state — QEMU's
        /// state save is built on its migration machinery, which only TCG and KVM implement — so
        /// memory snapshots are unavailable whenever hardware acceleration is in use. This is a
        /// platform limitation, not something the application can work around.
        /// </summary>
        public bool SupportsMemorySnapshots =>
            QemuArgumentBuilder.ResolveAccelerator(Config, _installation) == AcceleratorType.Tcg;

        /// <summary>
        /// Captures a snapshot including RAM, via the human monitor. QEMU only exposes <c>savevm</c>
        /// there; the job-based QMP equivalent requires naming every block node.
        /// Only available under TCG — see <see cref="SupportsMemorySnapshots"/>.
        /// </summary>
        public async Task SaveSnapshotAsync(string name, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A snapshot name is required.", nameof(name));

            if (!Config.Disks.Any(d => d.Format == DiskFormat.Qcow2))
                throw new InvalidOperationException("Snapshots require at least one qcow2 disk.");

            if (!SupportsMemorySnapshots)
            {
                throw new InvalidOperationException(
                    "A snapshot including memory cannot be taken while WHPX hardware acceleration is " +
                    "active, because WHPX cannot serialise CPU state. Take a disk-only snapshot of the " +
                    "running VM instead, or shut the VM down and snapshot it offline.");
            }

            var output = await RequireQmp()
                .ExecuteHumanCommandAsync($"savevm {name}", cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(output))
            {
                AppendLog($"savevm: {output.Trim()}");
                // The human monitor reports failures in its output rather than as a QMP error.
                if (output.Contains("Error", StringComparison.OrdinalIgnoreCase))
                    throw new QmpException(output.Trim());
            }

            AppendLog($"Snapshot '{name}' saved.");
        }

        public async Task RestoreSnapshotAsync(string name, CancellationToken cancellationToken = default)
        {
            var output = await RequireQmp()
                .ExecuteHumanCommandAsync($"loadvm {name}", cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(output))
            {
                AppendLog($"loadvm: {output.Trim()}");
                if (output.Contains("Error", StringComparison.OrdinalIgnoreCase))
                    throw new QmpException(output.Trim());
            }

            AppendLog($"Snapshot '{name}' restored.");
        }

        /// <summary>
        /// Takes a disk-only snapshot of the running VM. Each disk gets a fresh qcow2 overlay and the
        /// previous image becomes its backing file, so writes diverge from that point on.
        /// </summary>
        /// <remarks>
        /// The result is crash-consistent, not application-consistent: guest RAM and in-flight writes
        /// are not captured, so reverting looks to the guest like it lost power at this instant.
        /// This is the only snapshot form available under WHPX.
        /// </remarks>
        /// <returns>The overlay file created for each disk, keyed by disk id.</returns>
        public async Task<IReadOnlyDictionary<string, string>> CreateLiveDiskSnapshotAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A snapshot name is required.", nameof(name));

            var qmp = RequireQmp();
            var overlays = new Dictionary<string, string>();

            // qcow2 disks only, like internal snapshots. QEMU would layer an overlay on a raw image too,
            // but the descriptor holds one format per disk: it would then name a qcow2 overlay as "raw",
            // and the next start would hand the guest the overlay's bytes as a raw disk.
            foreach (var disk in VmSnapshotAggregator.SnapshotableDisks(Config))
            {
                var overlayPath = Path.Combine(
                    Config.FolderPath,
                    $"{disk.Id}-{SanitizeSnapshotName(name)}.qcow2");

                if (File.Exists(overlayPath))
                    throw new IOException($"A snapshot overlay already exists at {overlayPath}.");

                await qmp.ExecuteAsync(
                    "blockdev-snapshot-sync",
                    new JsonObject
                    {
                        ["device"] = disk.Id,
                        ["snapshot-file"] = overlayPath,
                        ["format"] = "qcow2"
                    },
                    cancellationToken).ConfigureAwait(false);

                overlays[disk.Id] = overlayPath;
                AppendLog($"Disk '{disk.Id}' now writes to the overlay {Path.GetFileName(overlayPath)}.");
            }

            return overlays;
        }

        private static string SanitizeSnapshotName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        /// <summary>
        /// Injects keyboard events into the guest through QMP.
        /// </summary>
        /// <param name="keys">
        /// Scancodes with their press state, delivered in order. <c>Number</c> is an AT set-1 scancode
        /// with 0x80 set for the extended (0xE0-prefixed) variants — the same encoding Windows reports
        /// through <c>KeyStatus.ScanCode</c> plus <c>IsExtendedKey</c>, so no translation table is needed.
        /// </param>
        /// <remarks>
        /// The console is embedded as a child window, and in that state SDL never enters the input-focus
        /// state QEMU requires before forwarding a key, so keystrokes cannot reach the guest through the
        /// window at all. Routing them over the control channel side-steps that entirely, and has the
        /// bonus that combinations Windows intercepts for itself can still be delivered.
        /// </remarks>
        public Task SendKeysAsync(IReadOnlyList<(int Number, bool Down)> keys, CancellationToken cancellationToken = default)
        {
            if (keys.Count == 0)
                return Task.CompletedTask;

            var events = new JsonArray();
            foreach (var (number, down) in keys)
            {
                events.Add(new JsonObject
                {
                    ["type"] = "key",
                    ["data"] = new JsonObject
                    {
                        ["down"] = down,
                        ["key"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["data"] = number
                        }
                    }
                });
            }

            return RequireQmp().ExecuteAsync(
                "input-send-event",
                new JsonObject { ["events"] = events },
                cancellationToken);
        }

        public Task SendKeyAsync(int number, bool down, CancellationToken cancellationToken = default) =>
            SendKeysAsync(new[] { (number, down) }, cancellationToken);

        /// <summary>
        /// Sends Ctrl+Alt+Del to the guest. Only reachable this way: Windows consumes that combination
        /// before any application sees it, so it can never arrive through the console window.
        /// </summary>
        public Task SendCtrlAltDeleteAsync(CancellationToken cancellationToken = default)
        {
            const int leftCtrl = 0x1D;
            const int leftAlt = 0x38;
            const int deleteExtended = 0x53 | 0x80;

            return SendKeysAsync(new[]
            {
                (leftCtrl, true), (leftAlt, true), (deleteExtended, true),
                (deleteExtended, false), (leftAlt, false), (leftCtrl, false)
            }, cancellationToken);
        }

        /// <summary>Ejects or swaps the medium in the primary optical drive while the VM runs.</summary>
        /// <summary>
        /// Swaps the medium in the primary optical drive of a running machine.
        /// </summary>
        /// <remarks>
        /// Addressed through the drive's qdev id, obtained from the argument builder so the two cannot
        /// drift. This targeted the legacy "ide1-cd0" until the optical drive became an explicit ide-cd
        /// device, at which point the command could only ever have failed.
        ///
        /// Structured QMP rather than the human monitor: an unknown id then comes back as an error
        /// instead of a line of prose that would have to be parsed to notice nothing happened.
        /// </remarks>
        public async Task ChangeCdromAsync(string? isoPath, CancellationToken cancellationToken = default)
        {
            var qmp = RequireQmp();
            var deviceId = QemuArgumentBuilder.DeviceIdFor(QemuArgumentBuilder.PrimaryCdId);

            if (string.IsNullOrWhiteSpace(isoPath))
            {
                await qmp.ExecuteAsync(
                    "eject",
                    new JsonObject { ["id"] = deviceId, ["force"] = true },
                    cancellationToken).ConfigureAwait(false);
                AppendLog("Optical drive ejected.");
                return;
            }

            var resolved = Config.ResolvePath(isoPath);
            await qmp.ExecuteAsync(
                "blockdev-change-medium",
                new JsonObject
                {
                    ["id"] = deviceId,
                    ["filename"] = resolved,
                    // An ISO is a raw image; stating it beats letting QEMU probe the format.
                    ["format"] = "raw"
                },
                cancellationToken).ConfigureAwait(false);
            AppendLog($"Optical drive now holds {resolved}.");
        }

        /// <summary>
        /// Opens a host forward on a running NAT adapter, without a restart. HMP only: QMP has no command
        /// for it. HMP answers in prose, empty on success.
        /// </summary>
        public async Task AddHostForwardAsync(string adapterId, PortForward forward, CancellationToken cancellationToken = default)
        {
            var reply = await RequireQmp().ExecuteHumanCommandAsync(
                $"hostfwd_add {adapterId} {Services.LanAccess.RuleOf(forward)}", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reply))
                throw new InvalidOperationException($"QEMU n'a pas ouvert le port {forward.HostPort} : {reply.Trim()}");
            AppendLog($"Host forward added: {Services.LanAccess.RuleOf(forward)}");
        }

        /// <summary>Closes a host forward on a running NAT adapter.</summary>
        public async Task RemoveHostForwardAsync(string adapterId, PortForward forward, CancellationToken cancellationToken = default)
        {
            var reply = await RequireQmp().ExecuteHumanCommandAsync(
                $"hostfwd_remove {adapterId} {Services.LanAccess.RemovalRuleOf(forward)}", cancellationToken).ConfigureAwait(false);
            // "host forwarding rule for ... removed" is the success message here, unlike hostfwd_add.
            if (reply.Contains("not found", StringComparison.OrdinalIgnoreCase))
                AppendLog($"Host forward was not open: {Services.LanAccess.RemovalRuleOf(forward)}");
            else
                AppendLog($"Host forward removed: {Services.LanAccess.RemovalRuleOf(forward)}");
        }

        /// <summary>
        /// The host USB devices handed to this machine, connected at start or since, and whether QEMU
        /// actually holds each one.
        /// </summary>
        /// <remarks>
        /// Read from QEMU each time rather than tracked here: a device recorded for every start is created
        /// by the command line, not by this class. "attached" is the honest signal — usb-host stays
        /// detached until libusb has opened the host device, and QEMU never reports a failed open.
        /// </remarks>
        public async Task<IReadOnlyList<(int VendorId, int ProductId, bool Attached)>> QueryUsbDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            var qmp = RequireQmp();
            var children = await qmp.ExecuteAsync(
                "qom-list", new JsonObject { ["path"] = "/machine/peripheral" }, cancellationToken).ConfigureAwait(false);

            var devices = new List<(int, int, bool)>();
            foreach (var child in children?.AsArray() ?? new JsonArray())
            {
                if (child?["name"]?.GetValue<string>() is not { } id || ParseUsbHostDeviceId(id) is not { } ids)
                    continue;
                devices.Add((ids.VendorId, ids.ProductId, await IsUsbAttachedAsync(qmp, id, cancellationToken).ConfigureAwait(false)));
            }

            return devices;
        }

        /// <summary>
        /// Hands a host USB device to the running guest. False when QEMU could not open it — unplugged,
        /// held by another program, or on a driver other than WinUSB — in which case nothing is left
        /// behind: a waiting usb-host would take the device later, out of the user's sight.
        /// </summary>
        public async Task<bool> ConnectUsbAsync(int vendorId, int productId, CancellationToken cancellationToken = default)
        {
            var qmp = RequireQmp();
            var id = QemuArgumentBuilder.UsbHostDeviceId(vendorId, productId);
            var created = true;

            try
            {
                await qmp.ExecuteAsync(
                    "device_add",
                    new JsonObject
                    {
                        ["driver"] = "usb-host",
                        ["id"] = id,
                        ["bus"] = QemuArgumentBuilder.UsbPassthroughControllerId + ".0",
                        ["vendorid"] = vendorId,
                        ["productid"] = productId
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (QmpException ex) when (ex.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
            {
                // Already there: recorded for every start and waiting for the device. Left in place if it
                // still cannot be opened, so it keeps taking the device whenever it becomes available.
                created = false;
            }

            // usb-host tries to open the device as it is realized, then every two seconds.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (await IsUsbAttachedAsync(qmp, id, cancellationToken).ConfigureAwait(false))
                {
                    AppendLog($"USB device {UsbDeviceConfig.FormatId(vendorId, productId)} connected.");
                    return true;
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            if (created)
                await qmp.ExecuteAsync("device_del", new JsonObject { ["id"] = id }, cancellationToken).ConfigureAwait(false);
            AppendLog($"USB device {UsbDeviceConfig.FormatId(vendorId, productId)} could not be opened by QEMU.");
            return false;
        }

        /// <summary>Gives a host USB device back to Windows. The guest sees it unplugged.</summary>
        public async Task DisconnectUsbAsync(int vendorId, int productId, CancellationToken cancellationToken = default)
        {
            await RequireQmp().ExecuteAsync(
                "device_del",
                new JsonObject { ["id"] = QemuArgumentBuilder.UsbHostDeviceId(vendorId, productId) },
                cancellationToken).ConfigureAwait(false);
            AppendLog($"USB device {UsbDeviceConfig.FormatId(vendorId, productId)} disconnected.");
        }

        private static async Task<bool> IsUsbAttachedAsync(QmpClient qmp, string id, CancellationToken cancellationToken)
        {
            var attached = await qmp.ExecuteAsync(
                "qom-get",
                new JsonObject { ["path"] = $"/machine/peripheral/{id}", ["property"] = "attached" },
                cancellationToken).ConfigureAwait(false);
            return attached?.GetValue<bool>() == true;
        }

        /// <summary>Vendor and product back from an id made by <see cref="QemuArgumentBuilder.UsbHostDeviceId"/>.</summary>
        public static (int VendorId, int ProductId)? ParseUsbHostDeviceId(string id)
        {
            var parts = id.Split('-');
            return parts.Length == 4 && id.StartsWith("wq-usb-", StringComparison.Ordinal)
                   && int.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor)
                   && int.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var product)
                ? (vendor, product)
                : null;
        }

        /// <summary>
        /// The guest's framebuffer size, asked of QEMU rather than guessed from its window.
        /// </summary>
        /// <remarks>
        /// screendump is the only place QEMU states the resolution outright: it writes a PPM whose header
        /// carries the exact dimensions. The SDL window is not a substitute — it is sized in host pixels,
        /// so a 150% display turns a 720x400 guest into a 1080x600 window, and once the shell has
        /// reparented and resized that window it says nothing about the guest at all.
        ///
        /// The cost is a full framebuffer written to a temporary file, so this is for attach time and
        /// mode changes, not for polling.
        /// </remarks>
        public async Task<(int Width, int Height)> QueryGuestScreenSizeAsync(CancellationToken cancellationToken = default)
        {
            var qmp = RequireQmp();
            var path = Path.Combine(Path.GetTempPath(), $"winqemu-screen-{Guid.NewGuid():N}.ppm");

            try
            {
                await qmp.ExecuteAsync(
                    "screendump",
                    new JsonObject { ["filename"] = path },
                    cancellationToken).ConfigureAwait(false);

                // Only the header is needed, and the body can be several megabytes.
                var header = new byte[64];
                await using (var stream = File.OpenRead(path))
                {
                    var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
                    return TryParsePpmSize(header.AsSpan(0, read), out var width, out var height)
                        ? (width, height)
                        : (0, 0);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (IOException)
                {
                    // A leftover temp file is not worth failing the query over.
                }
            }
        }

        /// <summary>
        /// Reads the dimensions out of a binary PPM header, which is "P6", then the width and height,
        /// then the maximum value, separated by any whitespace and possibly interrupted by # comments.
        /// </summary>
        public static bool TryParsePpmSize(ReadOnlySpan<byte> header, out int width, out int height)
        {
            width = 0;
            height = 0;

            var text = System.Text.Encoding.ASCII.GetString(header);
            if (!text.StartsWith("P6", StringComparison.Ordinal))
                return false;

            var numbers = new List<int>(2);
            var index = 2;
            while (index < text.Length && numbers.Count < 2)
            {
                var c = text[index];

                if (c == '#')
                {
                    // Comments run to the end of the line.
                    while (index < text.Length && text[index] != '\n')
                        index++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    index++;
                    continue;
                }

                if (!char.IsAsciiDigit(c))
                    return false;

                var start = index;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                    index++;

                // A number running to the very end of the buffer may be truncated, so it cannot be trusted.
                if (index >= text.Length)
                    return false;

                numbers.Add(int.Parse(text[start..index]));
            }

            if (numbers.Count < 2 || numbers[0] <= 0 || numbers[1] <= 0)
                return false;

            width = numbers[0];
            height = numbers[1];
            return true;
        }

        public async Task<VmMetrics> QueryMetricsAsync(CancellationToken cancellationToken = default)
        {
            var process = _process;
            if (process is null || process.HasExited)
                return default;

            double cpuPercent = 0;
            long residentBytes = 0;

            try
            {
                process.Refresh();
                residentBytes = process.WorkingSet64;

                var now = DateTime.UtcNow;
                var cpuTime = process.TotalProcessorTime;
                if (_lastCpuSampleUtc != default)
                {
                    var wallClockDelta = (now - _lastCpuSampleUtc).TotalMilliseconds;
                    if (wallClockDelta > 0)
                    {
                        var cpuDelta = (cpuTime - _lastCpuTime).TotalMilliseconds;
                        cpuPercent = Math.Clamp(
                            cpuDelta / (wallClockDelta * Environment.ProcessorCount) * 100d, 0d, 100d);
                    }
                }

                _lastCpuTime = cpuTime;
                _lastCpuSampleUtc = now;
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and the read.
                return default;
            }

            long read = 0;
            long written = 0;
            var qmp = _qmp;
            if (qmp is { IsConnected: true })
            {
                try
                {
                    var stats = await qmp.ExecuteAsync("query-blockstats", null, cancellationToken).ConfigureAwait(false);
                    if (stats is JsonArray devices)
                    {
                        foreach (var device in devices)
                        {
                            var counters = device?["stats"];
                            read += counters?["rd_bytes"]?.GetValue<long>() ?? 0;
                            written += counters?["wr_bytes"]?.GetValue<long>() ?? 0;
                        }
                    }
                }
                catch (Exception ex) when (ex is QmpException or ObjectDisposedException)
                {
                    // Statistics are best-effort; a dropped connection is handled by the watcher.
                }
            }

            return new VmMetrics(cpuPercent, residentBytes, read, written);
        }

        private void StartProcess(string executable, IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Creates QEMU's SDL windows hidden (STARTF_USESHOWWINDOW / SW_HIDE), so the console
                // never flashes up as a standalone window before the shell embeds it. The shell shows
                // it deliberately once it is a child of the console tab.
                WindowStyle = ProcessWindowStyle.Hidden,
                // QEMU loads its BIOS and keymap files relative to its own directory.
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            };

            // QEMU declares no DPI awareness, so its SDL window is DPI-unaware (measured: awareness 0) and
            // ends up a cross-process child of the per-monitor-aware shell. On a 150% display Windows then
            // scales between the two and the guest pointer lands about an arrow's width off the visible
            // cursor; the guest image is also drawn at 2/3 size and stretched back, so "Taille réelle" was
            // blurred. This SDL hint makes the window per-monitor aware (measured: awareness 2), in step
            // with the shell, which already sizes it in physical pixels.
            startInfo.Environment["SDL_WINDOWS_DPI_AWARENESS"] = "permonitorv2";

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            _processExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastQemuMessage = null;

            // Both streams must be drained: an unread pipe fills up and blocks QEMU mid-boot.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) CaptureOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) CaptureOutput(e.Data); };
            process.Exited += OnProcessExited;

            if (!process.Start())
                throw new InvalidOperationException($"Unable to start {executable}.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _lastCpuSampleUtc = default;
            _lastCpuTime = TimeSpan.Zero;
        }

        /// <summary>
        /// Records QEMU's output, keeping the last line that is not a mere warning. When a launch
        /// fails, that line is the actual reason and is far more useful than a connection timeout.
        /// </summary>
        private void CaptureOutput(string line)
        {
            if (!line.Contains("warning:", StringComparison.OrdinalIgnoreCase))
                _lastQemuMessage = line.Trim();

            AppendLog(line);
        }

        private async Task ConnectQmpAsync(int qmpPort, CancellationToken cancellationToken)
        {
            var connect = QmpClient.ConnectAsync(qmpPort, TimeSpan.FromSeconds(20), cancellationToken, AppendLog);
            var exited = _processExited?.Task ?? Task.Delay(Timeout.Infinite, cancellationToken);

            // A rejected command line kills QEMU within a second, but the QMP connect would keep
            // retrying for its full timeout and then report a misleading "handshake timed out".
            // Whichever happens first wins, so real failures surface immediately and in QEMU's words.
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited)
            {
                // Nobody will await the abandoned connect attempt; observe it so it cannot resurface
                // as an unobserved task exception.
                _ = connect.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

                var code = TryGetExitCode();
                throw new InvalidOperationException(
                    $"QEMU s'est arrêté (code {code}) avant d'accepter la connexion de contrôle. " +
                    (string.IsNullOrWhiteSpace(_lastQemuMessage)
                        ? "Consultez le journal pour le détail."
                        : _lastQemuMessage));
            }

            var qmp = await connect.ConfigureAwait(false);
            qmp.EventReceived += OnQmpEvent;
            _qmp = qmp;
        }

        private int TryGetExitCode()
        {
            try
            {
                return _process?.ExitCode ?? -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        private void OnQmpEvent(object? sender, QmpEventArgs e)
        {
            switch (e.Name)
            {
                case "STOP":
                    if (State == VmState.Running)
                        State = VmState.Paused;
                    break;

                case "RESUME":
                    if (State == VmState.Paused)
                        State = VmState.Running;
                    break;

                case "POWERDOWN":
                    AppendLog("Guest acknowledged the power button.");
                    break;

                case "SHUTDOWN":
                    State = VmState.Stopping;

                    // Under WHPX a reboot is turned into a shutdown (-action reboot=shutdown), because
                    // an in-place reset never comes back from UEFI firmware. The reason tells the two
                    // apart: "guest-reset" when the guest reboots, "host-qmp-system-reset" for the
                    // reset button. Either way the process is started again once it has exited.
                    var reason = e.Data?["reason"]?.GetValue<string>();
                    if (reason is "guest-reset" or "host-qmp-system-reset")
                    {
                        _restartAfterExit = true;
                        AppendLog("Reboot requested; QEMU will be restarted.");
                        break;
                    }

                    // QEMU emits SHUTDOWN both when the guest halts itself and when we cut power,
                    // so the reason decides which one the log claims happened.
                    AppendLog(e.Data?["guest"]?.GetValue<bool>() == true
                        ? "Guest has shut down."
                        : "Shutdown initiated from the host.");
                    break;

                case "RESET":
                    AppendLog("Guest reset.");
                    break;

                case "VSERPORT_CHANGE":
                    // Without an agent in the guest the clipboard channel is up but nothing answers;
                    // this is the only sign of whether spice-vdagent is there.
                    if (e.Data is { } port && port["id"]?.GetValue<string>() == QemuArgumentBuilder.ClipboardAgentPortId)
                    {
                        AppendLog(port["open"]?.GetValue<bool>() == true
                            ? "Clipboard: guest agent connected."
                            : "Clipboard: guest agent disconnected.");
                    }
                    break;

                case "GUEST_PANICKED":
                    LastError = "The guest OS panicked.";
                    AppendLog(LastError);
                    break;
            }
        }

        /// <summary>
        /// Puts text on the guest's clipboard. A no-op while the clipboard channel is down, so it is safe
        /// to call for every running VM on every host clipboard change.
        /// </summary>
        public async Task SetHostClipboardAsync(string text)
        {
            if (_clipboard is not { IsConnected: true } clipboard)
                return;

            try
            {
                await clipboard.SetHostTextAsync(text).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The channel went away with the VM; the exit path cleans it up.
            }
        }

        /// <summary>
        /// Opens the clipboard channel set up by <see cref="QemuArgumentBuilder"/>. Failure only costs the
        /// clipboard, never the VM, so it is logged and otherwise ignored.
        /// </summary>
        private async Task StartClipboardAsync(int port)
        {
            try
            {
                // VNC passwords are truncated to 8 characters; random per launch, never shown anywhere.
                var password = RandomNumberGenerator.GetString(
                    "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 8);
                await RequireQmp().ExecuteAsync(
                    "set_password",
                    new JsonObject { ["protocol"] = "vnc", ["password"] = password }).ConfigureAwait(false);

                var windowsGuest = GuestOsCatalog.Find(Config.GuestOs)?.Family == "Windows";
                var client = new VncClipboardClient(windowsGuest ? "\r\n" : "\n");
                client.GuestTextReceived += (_, text) => GuestClipboardChanged?.Invoke(this, text);
                client.Disconnected += (_, error) =>
                {
                    if (error is not null && State == VmState.Running)
                        AppendLog($"Clipboard: channel closed ({error.Message}).");
                };

                await client.ConnectAsync(port, password).ConfigureAwait(false);
                _clipboard = client;
                AppendLog("Clipboard: shared with the guest (spice-vdagent must be running inside it).");
                ClipboardReady?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppendLog($"Clipboard: not available ({ex.Message}).");
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            _processExited?.TrySetResult();

            var clipboard = _clipboard;
            _clipboard = null;
            if (clipboard is not null)
                _ = clipboard.DisposeAsync().AsTask();

            var exitCode = 0;
            try
            {
                exitCode = _process?.ExitCode ?? 0;
            }
            catch (InvalidOperationException)
            {
                // Exit code unavailable; treat it as a clean stop.
            }

            AppendLog($"QEMU exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.");

            var qmp = _qmp;
            _qmp = null;
            if (qmp is not null)
            {
                qmp.EventReceived -= OnQmpEvent;
                _ = qmp.DisposeAsync().AsTask();
            }

            var restart = _restartAfterExit && exitCode == 0;
            _restartAfterExit = false;

            // A non-zero exit during startup means QEMU rejected the configuration; the log holds why.
            if (exitCode != 0 && State is VmState.Starting or VmState.Running)
            {
                LastError = $"QEMU exited unexpectedly with code {exitCode.ToString(CultureInfo.InvariantCulture)}. " +
                            "See the log for details.";
                State = VmState.Error;
            }
            else
            {
                State = VmState.Stopped;
            }

            if (restart)
                _ = RestartAfterRebootAsync();
        }

        /// <summary>
        /// Starts the VM again after a reboot ended the process. The shell follows the state changes
        /// and re-attaches the console to the new window on its own.
        /// </summary>
        private async Task RestartAfterRebootAsync()
        {
            try
            {
                await StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // StartAsync already records the failure in LastError and the Error state; this only
                // keeps the exception from going unobserved.
                AppendLog($"Restart after reboot failed: {ex.Message}");
            }
        }

        private async Task KillProcessAsync()
        {
            var process = _process;
            if (process is null)
                return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        /// <summary>
        /// Creates this VM's private UEFI variable store from the firmware template on first boot.
        /// It holds boot entries and Secure Boot keys, so it must never be shared between VMs.
        /// </summary>
        private void EnsureUefiVarsStore()
        {
            if (Config.Firmware == FirmwareType.Bios)
                return;

            var target = QemuArgumentBuilder.GetUefiVarsPath(Config);
            if (File.Exists(target))
                return;

            var template = Config.Architecture == GuestArchitecture.Aarch64
                ? _installation.UefiVarsTemplateAarch64
                : _installation.UefiVarsTemplateX64;

            if (template is null)
                throw new QemuNotAvailableException(
                    $"The UEFI variable template for {Config.Architecture} was not found in {_installation.InstallDirectory}.");

            Directory.CreateDirectory(Config.FolderPath);
            File.Copy(template, target);
            AppendLog($"Created the UEFI variable store at {target}.");
        }

        private volatile IReadOnlyList<Sharing.SharedFolderEntry> _sharedFolders = Array.Empty<Sharing.SharedFolderEntry>();

        /// <summary>
        /// What the guest sees under its shared folders mount. Called by the shell on its own thread
        /// whenever the list changes; the server reads the snapshot from its own.
        /// </summary>
        public void UpdateSharedFolders(IEnumerable<SharedFolderConfig> folders) =>
            _sharedFolders = folders.Select(f => new Sharing.SharedFolderEntry(f.GuestName, f.HostPath, f.ReadOnly)).ToList();

        /// <summary>
        /// Serves the shared folders on their virtio-serial port for as long as QEMU runs. The connection
        /// lasts the whole run: the guest opening and closing the port only starts a new 9P session on it.
        /// </summary>
        private async Task ServeSharedFoldersAsync(int port)
        {
            var server = new Sharing.NinePServer(() => _sharedFolders);
            while (IsRunning)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    await server.ServeAsync(client.GetStream()).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
                {
                    if (IsRunning)
                        AppendLog($"Shared folders: {ex.Message}");
                }

                if (IsRunning)
                    await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        /// <summary>Loopback port of the qemu-guest-agent channel of the current run.</summary>
        public int? GuestAgentPort { get; private set; }

        /// <summary>
        /// A connection to the guest agent. It may lead nowhere: whether an agent answers is for
        /// <see cref="GuestAgentClient.SynchronizeAsync"/> to find out.
        /// </summary>
        public Task<GuestAgentClient> ConnectGuestAgentAsync(CancellationToken cancellationToken = default) =>
            IsRunning && GuestAgentPort is { } port
                ? GuestAgentClient.ConnectAsync(port, cancellationToken)
                : throw new InvalidOperationException("The VM is not running.");

        private QmpClient RequireQmp() =>
            _qmp is { IsConnected: true } qmp
                ? qmp
                : throw new InvalidOperationException("The VM is not running, or its QMP connection was lost.");

        /// <summary>
        /// Asks the OS for a free loopback port by binding to port 0, then releases it for QEMU.
        /// There is a small race window here, which is why the QMP connect retries.
        /// </summary>
        private static int ReserveLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private void AppendLog(string line)
        {
            var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            lock (_log)
            {
                _log.Add(stamped);
                if (_log.Count > MaxLogLines)
                    _log.RemoveRange(0, _log.Count - MaxLogLines);
            }

            LogLineAppended?.Invoke(this, stamped);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                await PowerOffAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort during teardown.
            }

            var process = _process;
            if (process is not null)
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
                _process = null;
            }

            var qmp = _qmp;
            _qmp = null;
            if (qmp is not null)
                await qmp.DisposeAsync().ConfigureAwait(false);

            _transitionLock.Dispose();
        }
    }
}
