using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private const int MaxDisplayedLogLines = 500;

        private readonly DispatcherQueue _dispatcher;
        private readonly ClipboardBridge _clipboard;
        private readonly DispatcherQueueTimer _metricsTimer;
        private readonly DispatcherQueueTimer _saveDebounceTimer;
        private readonly DispatcherQueueTimer _logFlushTimer;
        private readonly List<string> _logBuffer = new();

        private VmSession? _observedSession;

        public MainViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            Settings = AppSettings.Load();
            Manager = new VmManager(Settings);

            // Follows every session the manager creates, so it must exist before the first one is.
            _clipboard = new ClipboardBridge(Manager, _dispatcher) { Log = AppendDiagnostic };

            RefreshCommand = new AsyncRelayCommand(_ => InitializeAsync(), onError: ReportError);
            StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => CanStart, ReportError);
            PauseResumeCommand = new AsyncRelayCommand(_ => PauseOrResumeAsync(), _ => CanPauseOrResume, ReportError);
            ShutdownCommand = new AsyncRelayCommand(_ => ShutdownAsync(), _ => IsSelectedRunning, ReportError);
            PowerOffCommand = new AsyncRelayCommand(_ => PowerOffAsync(), _ => IsSelectedAlive, ReportError);
            DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(p is true), _ => SelectedVm is not null, ReportError);
            SendCtrlAltDeleteCommand = new AsyncRelayCommand(_ => SendCtrlAltDeleteAsync(), _ => IsSelectedRunning, ReportError);

            // Metrics come from sampling the QEMU process and QMP, so they are polled rather than pushed.
            _metricsTimer = _dispatcher.CreateTimer();
            _metricsTimer.Interval = TimeSpan.FromSeconds(1);
            _metricsTimer.Tick += (_, _) => _ = RefreshMetricsAsync();

            // Editing a name or CPU count fires PropertyChanged per keystroke; debouncing keeps that
            // from rewriting the descriptor on every character.
            _saveDebounceTimer = _dispatcher.CreateTimer();
            _saveDebounceTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveDebounceTimer.IsRepeating = false;
            _saveDebounceTimer.Tick += (_, _) => SaveSelected();

            // QEMU can emit output in bursts. Rebuilding the log text once per burst keeps this off
            // the per-line path, where it would be quadratic.
            _logFlushTimer = _dispatcher.CreateTimer();
            _logFlushTimer.Interval = TimeSpan.FromMilliseconds(200);
            _logFlushTimer.IsRepeating = false;
            _logFlushTimer.Tick += (_, _) => FlushLog();

            PropertyChanged += OnSelfPropertyChanged;

            // Hardware edits reuse the same debounce as the text fields: reordering the boot list or
            // typing a port number should not rewrite the descriptor on every keystroke.
            Hardware = new VmHardwareViewModel(Manager, RestartSaveDebounce, ReportError);
            Snapshots = new VmSnapshotsViewModel(Manager, RestartSaveDebounce, ReportError);

            // The library is reloaded on refresh, creation and deletion; the inventory rows follow it.
            Manager.VirtualMachines.CollectionChanged += (_, _) => RebuildItems();
        }

        /// <summary>Boot order, disks and network adapters of the selected machine.</summary>
        public VmHardwareViewModel Hardware { get; }

        /// <summary>Disk snapshots of the selected machine.</summary>
        public VmSnapshotsViewModel Snapshots { get; }

        public AppSettings Settings { get; }

        public VmManager Manager { get; }

        public ObservableCollection<VmConfig> VMs => Manager.VirtualMachines;

        private readonly List<VmListItem> _allItems = new();

        /// <summary>The inventory as shown, i.e. after the search filter.</summary>
        public ObservableCollection<VmListItem> Items { get; } = new();

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
        }

        private VmListItem? _selectedItem;
        public VmListItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (!SetProperty(ref _selectedItem, value))
                    return;

                // Remembered separately, and only when non-null: emptying the bound collection makes
                // the ListView null its own selection, so this property cannot be used as the memory
                // of what the user had chosen — it is already gone by the time filtering finishes.
                if (value is not null)
                    _intendedSelection = value.Config;

                // The list is the only place selection originates; SelectedVm follows it.
                SelectedVm = value?.Config;
            }
        }

        private VmConfig? _intendedSelection;

        private string? _nameError;
        /// <summary>
        /// Why the selected machine cannot be saved under its current name, or null when it can. Set by
        /// the save path rather than by the setter of Name, so it also covers a name arriving from
        /// anywhere else.
        /// </summary>
        public string? NameError
        {
            get => _nameError;
            private set
            {
                if (SetProperty(ref _nameError, value))
                    RaisePropertyChanged(nameof(HasNameError));
            }
        }

        public bool HasNameError => NameError is not null;

        public bool HasNoVirtualMachines => _allItems.Count == 0;

        public bool HasNoSearchResults => _allItems.Count > 0 && Items.Count == 0;

        /// <summary>
        /// Rebuilds the inventory rows. Each row needs its VM's session to observe state, so a session
        /// is materialised for every machine — that only allocates, it starts nothing.
        /// </summary>
        private void RebuildItems()
        {
            var previous = SelectedVm;

            foreach (var item in _allItems)
                item.Dispose();
            _allItems.Clear();

            foreach (var config in Manager.VirtualMachines)
            {
                VmSession? session = null;
                try
                {
                    session = Manager.IsQemuAvailable ? Manager.GetSession(config) : Manager.FindSession(config);
                }
                catch (QemuNotAvailableException)
                {
                    // Without QEMU the row still lists the machine, just without live state.
                }

                _allItems.Add(new VmListItem(config, session, _dispatcher));
            }

            ApplyFilter();

            SelectedItem = _allItems.FirstOrDefault(i => ReferenceEquals(i.Config, previous))
                ?? Items.FirstOrDefault();
        }

        private void ApplyFilter()
        {
            Items.Clear();
            foreach (var item in _allItems.Where(i => i.Matches(SearchText)))
                Items.Add(item);

            // Restore what the user had chosen if it survived the filter, otherwise fall back to the
            // first visible row. Without this, typing in the search box silently deselected the machine.
            var wanted = _allItems.FirstOrDefault(i => ReferenceEquals(i.Config, _intendedSelection));
            SelectedItem = wanted is not null && Items.Contains(wanted) ? wanted : Items.FirstOrDefault();

            RaisePropertyChanged(nameof(HasNoVirtualMachines));
            RaisePropertyChanged(nameof(HasNoSearchResults));
        }

        private string _logText = string.Empty;
        /// <summary>
        /// The session log as one block of text. Deliberately not a collection bound to a ListView:
        /// a couple of thousand list items make the accessibility tree enormous, which wedges the UI
        /// thread whenever a screen reader or automation client walks it.
        /// </summary>
        public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

        /// <summary>Raised after <see cref="LogText"/> changes so the view can scroll to the end.</summary>
        public event EventHandler? LogUpdated;

        // ---- Commands ----

        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand StartCommand { get; }
        public AsyncRelayCommand PauseResumeCommand { get; }
        public AsyncRelayCommand ShutdownCommand { get; }
        public AsyncRelayCommand PowerOffCommand { get; }
        public AsyncRelayCommand DeleteCommand { get; }
        public AsyncRelayCommand SendCtrlAltDeleteCommand { get; }

        // ---- Selection ----

        private VmConfig? _selectedVm;
        /// <summary>
        /// Configuration behind the current selection. Driven by <see cref="SelectedItem"/>, which the
        /// inventory list binds to — a single path in keeps the two from drifting apart.
        /// </summary>
        public VmConfig? SelectedVm
        {
            get => _selectedVm;
            private set
            {
                if (ReferenceEquals(_selectedVm, value))
                    return;

                if (_selectedVm is not null)
                    _selectedVm.PropertyChanged -= OnSelectedVmPropertyChanged;

                SetProperty(ref _selectedVm, value);

                if (_selectedVm is not null)
                    _selectedVm.PropertyChanged += OnSelectedVmPropertyChanged;

                // A refusal belongs to the machine it was raised for, not to the next one selected.
                NameError = null;

                Hardware.Attach(_selectedVm);
                Snapshots.Attach(_selectedVm);
                AttachSession();
            }
        }

        private VmSession? _selectedSession;
        public VmSession? SelectedSession
        {
            get => _selectedSession;
            private set => SetProperty(ref _selectedSession, value);
        }

        // ---- Derived state ----

        public VmState SelectedState => SelectedSession?.State ?? VmState.Stopped;

        public string SelectedStateText => SelectedVm is null
            ? "Aucune VM sélectionnée"
            : SelectedState switch
            {
                VmState.Stopped => "Arrêtée",
                VmState.Starting => "Démarrage…",
                VmState.Running => "En cours d'exécution",
                VmState.Paused => "En pause",
                VmState.Stopping => "Arrêt en cours…",
                VmState.Error => "Erreur",
                _ => SelectedState.ToString()
            };

        public bool IsSelectedRunning => SelectedSession?.IsRunning == true;

        /// <summary>
        /// True while a QEMU process still exists for the selection, including a graceful shutdown
        /// that has not completed. Cutting the power has to stay available then — a guest that ignores
        /// the ACPI button (a boot menu, a hung system) would otherwise leave the VM with no way out.
        /// </summary>
        public bool IsSelectedAlive =>
            SelectedSession is { } session
            && session.State is VmState.Running or VmState.Paused or VmState.Stopping;

        /// <summary>
        /// Process whose console should be embedded, or null when there is nothing to show.
        /// The view watches <see cref="ConsoleTargetChanged"/> rather than polling.
        /// </summary>
        /// <remarks>
        /// Stopping counts as showable: an ACPI shutdown is exactly when the user wants to watch the
        /// guest, and detaching at that point also handed the console back as a separate framed window
        /// while QEMU was still alive.
        /// </remarks>
        public int? ConsoleProcessId =>
            SelectedSession is { } session
            && session.State is VmState.Running or VmState.Paused or VmState.Stopping
                ? session.ProcessId
                : null;

        /// <summary>Raised whenever <see cref="ConsoleProcessId"/> may have changed. Always on the UI thread.</summary>
        public event EventHandler? ConsoleTargetChanged;

        public bool CanStart => SelectedVm is not null && Manager.IsQemuAvailable && SelectedState == VmState.Stopped;

        public bool CanPauseOrResume => SelectedState is VmState.Running or VmState.Paused;

        /// <summary>
        /// "Pause" rather than "Suspendre": the command freezes execution and leaves the state in
        /// memory, which is not the suspend-to-disk that word suggests, and it pairs with "Reprendre".
        /// </summary>
        public string PauseResumeLabel => SelectedState == VmState.Paused ? "Reprendre" : "Pause";

        // ---- QEMU availability ----

        public bool IsQemuAvailable => Manager.IsQemuAvailable;

        /// <summary>
        /// True only once a probe has actually run and come back empty-handed. Without the gate the
        /// property is true before detection starts, so the "QEMU introuvable" banner flashed for the
        /// second or so the probe takes — reporting a failure that had not happened yet.
        /// </summary>
        public bool IsQemuMissing => _hasProbedQemu && !Manager.IsQemuAvailable;

        private bool _hasProbedQemu;

        private string _qemuStatusText = "Détection de QEMU…";
        public string QemuStatusText { get => _qemuStatusText; private set => SetProperty(ref _qemuStatusText, value); }

        private string _qemuStatusDetail = string.Empty;
        public string QemuStatusDetail { get => _qemuStatusDetail; private set => SetProperty(ref _qemuStatusDetail, value); }

        // ---- Settings surface ----

        /// <summary>
        /// Where QEMU was actually found, or how it is being looked for. Shows the configured override
        /// even when detection failed, so a wrong path is visible rather than merely ineffective.
        /// </summary>
        public string QemuDirectoryDisplay =>
            Manager.Installation?.InstallDirectory
            ?? (string.IsNullOrWhiteSpace(Settings.QemuDirectory)
                ? "Détection automatique — rien trouvé"
                : Settings.QemuDirectory);

        public bool IsQemuDirectoryOverridden => !string.IsNullOrWhiteSpace(Settings.QemuDirectory);

        public string QemuVersionDisplay => Manager.Installation?.Version ?? "—";

        public string AccelerationDisplay => Manager.Installation is null
            ? "—"
            : Manager.Installation.SupportsWhpx
                ? "WHPX disponible"
                : "Indisponible — les machines tourneront en émulation, environ dix fois plus lentement";

        public string ArchitecturesDisplay => Manager.Installation is null
            ? "—"
            : string.Join(", ", Manager.Installation.SystemBinaries.Keys.Select(DescribeArchitecture));

        /// <summary>
        /// Whether the OVMF images shipped with this build are present. Their absence is invisible
        /// until a UEFI machine refuses to start, so it belongs on the settings screen.
        /// </summary>
        public string FirmwareDisplay
        {
            get
            {
                var installation = Manager.Installation;
                if (installation is null)
                    return "—";

                var available = new List<string>();
                if (installation.SupportsUefi(GuestArchitecture.X86_64, secureBoot: false))
                    available.Add("UEFI x86-64");
                if (installation.SupportsUefi(GuestArchitecture.Aarch64, secureBoot: false))
                    available.Add("UEFI ARM64");

                return available.Count == 0
                    ? "Aucune image OVMF trouvée — seul le démarrage BIOS est possible"
                    : string.Join(", ", available) + " — le démarrage sécurisé reste impossible sous WHPX";
            }
        }

        private static string DescribeArchitecture(GuestArchitecture architecture) => architecture switch
        {
            GuestArchitecture.X86_64 => "x86-64",
            GuestArchitecture.Aarch64 => "ARM64",
            _ => architecture.ToString()
        };

        public string LibraryRootDisplay => Settings.EffectiveLibraryRoot;

        public string DiagnosticsText => string.Join(Environment.NewLine, Manager.Warnings);

        public bool HasDiagnostics => Manager.Warnings.Count > 0;

        /// <summary>Points the shell at an explicit QEMU folder and re-runs detection.</summary>
        public async Task SetQemuDirectoryAsync(string path)
        {
            Settings.QemuDirectory = path;
            Settings.Save();
            await InitializeAsync();
        }

        /// <summary>Clears the override so detection probes the usual locations again.</summary>
        public async Task ClearQemuDirectoryAsync() => await SetQemuDirectoryAsync(string.Empty);

        public void SetLibraryRoot(string path)
        {
            Manager.SetLibraryRoot(path);
            RaiseSettingsProperties();
        }

        private void RaiseSettingsProperties()
        {
            RaisePropertyChanged(nameof(QemuDirectoryDisplay));
            RaisePropertyChanged(nameof(IsQemuDirectoryOverridden));
            RaisePropertyChanged(nameof(QemuVersionDisplay));
            RaisePropertyChanged(nameof(AccelerationDisplay));
            RaisePropertyChanged(nameof(ArchitecturesDisplay));
            RaisePropertyChanged(nameof(FirmwareDisplay));
            RaisePropertyChanged(nameof(LibraryRootDisplay));
            RaisePropertyChanged(nameof(DiagnosticsText));
            RaisePropertyChanged(nameof(HasDiagnostics));
        }

        // ---- Errors ----

        private string _errorMessage = string.Empty;
        public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public void ClearError() => ErrorMessage = string.Empty;

        // ---- Metrics ----

        private double _cpuPercent;
        public double CpuPercent { get => _cpuPercent; private set => SetProperty(ref _cpuPercent, value); }

        private double _memoryPercent;
        public double MemoryPercent { get => _memoryPercent; private set => SetProperty(ref _memoryPercent, value); }

        private string _cpuText = "CPU —";
        public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }

        private string _memoryText = "Mémoire —";
        public string MemoryText { get => _memoryText; private set => SetProperty(ref _memoryText, value); }

        private string _diskText = "Disque —";
        public string DiskText { get => _diskText; private set => SetProperty(ref _diskText, value); }

        // ---- Lifecycle ----

        public async Task InitializeAsync()
        {
            await Manager.InitializeAsync();
            _hasProbedQemu = true;

            var installation = Manager.Installation;
            if (installation is null)
            {
                QemuStatusText = "QEMU est introuvable. Aucune machine virtuelle ne peut démarrer.";
                QemuStatusDetail = Manager.Warnings.Count > 0
                    ? string.Join(Environment.NewLine, Manager.Warnings)
                    : "Installez QEMU pour Windows, puis relancez la détection. " +
                      "Vous pouvez aussi indiquer le dossier d'installation dans les paramètres.";
            }
            else
            {
                var accelerator = installation.SupportsWhpx
                    ? "accélération WHPX disponible"
                    : "accélération matérielle indisponible (émulation TCG uniquement)";

                QemuStatusText = $"QEMU {installation.Version ?? "?"} — {installation.InstallDirectory} ({accelerator}).";
                QemuStatusDetail = string.Join(Environment.NewLine, Manager.Warnings);
            }

            RaisePropertyChanged(nameof(IsQemuAvailable));
            RaisePropertyChanged(nameof(IsQemuMissing));
            RaiseSettingsProperties();

            // Sessions only become available once QEMU is found, so the rows are built after detection
            // rather than relying on the collection-changed hook alone.
            RebuildItems();
        }

        /// <summary>
        /// Forwards keystrokes captured by the console host to the guest over QMP.
        /// </summary>
        /// <remarks>
        /// Failures are swallowed on purpose. This runs for every key edge, and a VM that stopped
        /// mid-keystroke would otherwise raise an error dialog per key.
        /// </remarks>
        public async Task ForwardKeysAsync(IReadOnlyList<(int Number, bool Down)> keys)
        {
            var session = SelectedSession;
            if (session is null || session.State is not (VmState.Running or VmState.Paused))
                return;

            try
            {
                await session.SendKeysAsync(keys);
            }
            catch (Exception ex) when (ex is QmpException or InvalidOperationException or ObjectDisposedException)
            {
                // The control channel went away; the state machine reports that on its own.
            }
        }

        private async Task SendCtrlAltDeleteAsync()
        {
            if (SelectedSession is { } session && session.State == VmState.Running)
                await session.SendCtrlAltDeleteAsync();
        }

        /// <summary>Creates a VM from what the wizard assembled and selects it.</summary>
        public async Task<VmConfig> CreateFromRequestAsync(CreateVmRequest request)
        {
            // Adding to the library raises CollectionChanged, which rebuilds the rows; selecting the
            // new machine has to happen after that.
            var created = await Manager.CreateVirtualMachineAsync(request);
            SelectConfig(created);
            return created;
        }

        private void SelectConfig(VmConfig? config) =>
            SelectedItem = config is null
                ? null
                : _allItems.FirstOrDefault(item => ReferenceEquals(item.Config, config));

        private async Task StartAsync()
        {
            if (SelectedSession is null)
                return;

            ClearError();
            var config = SelectedSession.Config;

            await SelectedSession.StartAsync();

            // Stamped here rather than in the core: this continuation is back on the UI thread, which
            // is the only thread allowed to raise PropertyChanged on a data-bound config.
            config.LastStartedUtc = DateTime.UtcNow;
        }

        private Task PauseOrResumeAsync()
        {
            if (SelectedSession is null)
                return Task.CompletedTask;

            return SelectedSession.State == VmState.Paused
                ? SelectedSession.ResumeAsync()
                : SelectedSession.PauseAsync();
        }

        private Task ShutdownAsync() => SelectedSession?.RequestShutdownAsync() ?? Task.CompletedTask;

        private Task PowerOffAsync() => SelectedSession?.PowerOffAsync() ?? Task.CompletedTask;

        private async Task DeleteAsync(bool deleteFiles)
        {
            var target = SelectedVm;
            if (target is null)
                return;

            // Cleared first so nothing keeps observing a machine that is going away; removing it from
            // the library then rebuilds the rows and picks a new selection.
            SelectedItem = null;
            await Manager.DeleteVirtualMachineAsync(target, deleteFiles);
        }

        // ---- Session plumbing ----

        private void AttachSession()
        {
            if (_observedSession is not null)
            {
                _observedSession.StateChanged -= OnSessionStateChanged;
                _observedSession.LogLineAppended -= OnSessionLogLineAppended;
                _observedSession = null;
            }

            _logBuffer.Clear();
            LogText = string.Empty;
            ResetMetrics();

            VmSession? session = null;
            if (SelectedVm is not null && Manager.IsQemuAvailable)
            {
                try
                {
                    session = Manager.GetSession(SelectedVm);
                }
                catch (QemuNotAvailableException ex)
                {
                    ErrorMessage = ex.Message;
                    RaisePropertyChanged(nameof(HasError));
                }
            }

            SelectedSession = session;

            if (session is not null)
            {
                _logBuffer.AddRange(session.Log.TakeLast(MaxDisplayedLogLines));
                FlushLog();

                session.StateChanged += OnSessionStateChanged;
                session.LogLineAppended += OnSessionLogLineAppended;

                // Recorded so the next selection change can unsubscribe. Without this the block above
                // had nothing to detach and every reselection stacked another handler pair on the same
                // session — the log then showed each QEMU line once per accumulated subscription.
                _observedSession = session;
            }

            RaiseStateDependentProperties();
            UpdateMetricsTimer();
            NotifyConsoleTargetChanged();
        }

        private void NotifyConsoleTargetChanged()
        {
            RaisePropertyChanged(nameof(ConsoleProcessId));
            ConsoleTargetChanged?.Invoke(this, EventArgs.Empty);
        }

        // Session events arrive on background threads; everything below must hop to the UI thread.

        private void OnSessionStateChanged(object? sender, VmState state)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(sender, SelectedSession))
                    return;

                RaiseStateDependentProperties();
                UpdateMetricsTimer();
                NotifyConsoleTargetChanged();

                if (state == VmState.Error && SelectedSession?.LastError is { Length: > 0 } error)
                {
                    ErrorMessage = error;
                    RaisePropertyChanged(nameof(HasError));
                }
            });
        }

        private void OnSessionLogLineAppended(object? sender, string line)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(sender, SelectedSession))
                    return;

                _logBuffer.Add(line);
                if (_logBuffer.Count > MaxDisplayedLogLines)
                    _logBuffer.RemoveRange(0, _logBuffer.Count - MaxDisplayedLogLines);

                // Coalesce bursts rather than rebuilding the text for every single line.
                _logFlushTimer.Stop();
                _logFlushTimer.Start();
            });
        }

        /// <summary>
        /// Appends a shell-side diagnostic to the same log as QEMU's output, so console embedding
        /// problems are visible next to the VM they belong to. Safe to call from any thread.
        /// </summary>
        public void AppendDiagnostic(string message)
        {
            _dispatcher.TryEnqueue(() =>
            {
                _logBuffer.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
                if (_logBuffer.Count > MaxDisplayedLogLines)
                    _logBuffer.RemoveRange(0, _logBuffer.Count - MaxDisplayedLogLines);

                _logFlushTimer.Stop();
                _logFlushTimer.Start();
            });
        }

        private void FlushLog()
        {
            LogText = string.Join(Environment.NewLine, _logBuffer);
            LogUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseStateDependentProperties()
        {
            RaisePropertyChanged(nameof(SelectedState));
            RaisePropertyChanged(nameof(SelectedStateText));
            RaisePropertyChanged(nameof(IsSelectedRunning));
            RaisePropertyChanged(nameof(IsSelectedAlive));
            RaisePropertyChanged(nameof(CanStart));
            RaisePropertyChanged(nameof(CanPauseOrResume));
            RaisePropertyChanged(nameof(PauseResumeLabel));
            RaisePropertyChanged(nameof(CanChangeIsoLive));

            // Boot order, disks and adapters are only read when QEMU starts, so they stay locked for
            // as long as a process exists — including while it is stopping.
            Hardware.CanEdit = SelectedVm is not null && !IsSelectedAlive;

            // qemu-img cannot touch an image a running QEMU holds open, so snapshots wait for the
            // machine to be fully down, not merely stopping.
            Snapshots.IsMachineOff = SelectedVm is not null && !IsSelectedAlive;
            // Memory and live snapshots go through the running QEMU, so the section needs the session.
            Snapshots.Session = SelectedSession;

            StartCommand.RaiseCanExecuteChanged();
            SendCtrlAltDeleteCommand.RaiseCanExecuteChanged();
            PauseResumeCommand.RaiseCanExecuteChanged();
            ShutdownCommand.RaiseCanExecuteChanged();
            PowerOffCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }

        // ---- Metrics ----

        private void UpdateMetricsTimer()
        {
            if (SelectedSession?.IsRunning == true)
            {
                if (!_metricsTimer.IsRunning)
                    _metricsTimer.Start();
            }
            else
            {
                _metricsTimer.Stop();
                ResetMetrics();
            }
        }

        private void ResetMetrics()
        {
            CpuPercent = 0;
            MemoryPercent = 0;
            CpuText = "CPU —";
            MemoryText = "Mémoire —";
            DiskText = "Disque —";
        }

        private async Task RefreshMetricsAsync()
        {
            var session = SelectedSession;
            if (session is null || !session.IsRunning)
                return;

            VmMetrics metrics;
            try
            {
                metrics = await session.QueryMetricsAsync();
            }
            catch (Exception ex) when (ex is QmpException or InvalidOperationException or ObjectDisposedException)
            {
                return;
            }

            var allocatedBytes = (double)session.Config.MemoryMb * 1024 * 1024;

            CpuPercent = metrics.HostCpuPercent;
            CpuText = $"CPU hôte {metrics.HostCpuPercent.ToString("0.#", CultureInfo.CurrentCulture)} %";

            MemoryPercent = allocatedBytes > 0
                ? Math.Clamp(metrics.ResidentMemoryBytes / allocatedBytes * 100d, 0d, 100d)
                : 0d;
            MemoryText = $"Mémoire {FormatBytes(metrics.ResidentMemoryBytes)} / {FormatBytes((long)allocatedBytes)}";

            DiskText = $"Disque lu {FormatBytes(metrics.DiskReadBytes)} · écrit {FormatBytes(metrics.DiskWrittenBytes)}";
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "o", "Kio", "Mio", "Gio", "Tio" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
        }

        // ---- Persistence ----

        private void OnSelectedVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The core mutates the config from background threads too — VmSession stamps
            // LastStartedUtc once the VM is up. A DispatcherQueueTimer may only be touched from
            // its own thread, so this has to marshal rather than assume a UI-thread caller.
            if (_dispatcher.HasThreadAccess)
                RestartSaveDebounce();
            else
                _dispatcher.TryEnqueue(RestartSaveDebounce);
        }

        // Restart the debounce window on every edit so only the final value hits the disk.
        private void RestartSaveDebounce()
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        /// <summary>
        /// Points the optical drive at another image. On a running machine the medium is swapped live
        /// over QMP, which is the one hardware change QEMU accepts without a restart.
        /// </summary>
        public async Task ChangeIsoAsync(string? path)
        {
            var target = SelectedVm;
            if (target is null)
                return;

            var session = SelectedSession;
            var wasRunning = session is not null && session.IsRunning;

            try
            {
                if (wasRunning)
                    await session!.ChangeCdromAsync(path);

                target.IsoPath = path ?? string.Empty;
                SaveSelected();

                AppendDiagnostic(wasRunning
                    ? string.IsNullOrWhiteSpace(path)
                        ? "Lecteur optique éjecté."
                        : $"Lecteur optique : {path}"
                    : "Image ISO enregistrée ; elle sera présentée au prochain démarrage.");
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        /// <summary>True when the medium can be swapped without restarting, i.e. the machine is running.</summary>
        public bool CanChangeIsoLive => SelectedSession?.IsRunning == true;

        private void SaveSelected()
        {
            var target = SelectedVm;
            if (target is null)
                return;

            // The descriptor is named after the machine, so a name the filesystem cannot take blocks the
            // whole save rather than just the name. Reported next to the field instead of through the
            // error bar: this fires on every keystroke while a name is being typed, and the field is
            // where the user is looking.
            NameError = VmLibrary.ValidateName(target.Name);
            if (NameError is not null)
                return;

            try
            {
                Manager.Save(target);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ErrorMessage))
                RaisePropertyChanged(nameof(HasError));
        }

        private void ReportError(Exception exception)
        {
            _dispatcher.TryEnqueue(() =>
            {
                ErrorMessage = exception.Message;
                RaisePropertyChanged(nameof(HasError));
            });
        }
    }
}
