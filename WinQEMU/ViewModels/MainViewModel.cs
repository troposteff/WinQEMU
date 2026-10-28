using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;
using WinQEMU.Core.Sharing;

namespace WinQEMU.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private const int MaxDisplayedLogLines = 500;

        private readonly DispatcherQueue _dispatcher;
        private readonly ClipboardBridge _clipboard;
        private readonly DispatcherQueueTimer _metricsTimer;
        private readonly DispatcherQueueTimer _audioTimer;
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
            Manager.SessionCreated += (_, session) => session.StateChanged += OnAnySessionStateChanged;

            RefreshCommand = new AsyncRelayCommand(_ => InitializeAsync(), onError: ReportError);
            StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => CanStart, ReportError);
            PauseResumeCommand = new AsyncRelayCommand(_ => PauseOrResumeAsync(), _ => CanPauseOrResume, ReportError);
            ShutdownCommand = new AsyncRelayCommand(_ => ShutdownAsync(), _ => IsSelectedRunning, ReportError);
            PowerOffCommand = new AsyncRelayCommand(_ => PowerOffAsync(), _ => IsSelectedAlive, ReportError);
            DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(p is true), _ => SelectedVm is not null, ReportError);
            SendCtrlAltDeleteCommand = new AsyncRelayCommand(_ => SendCtrlAltDeleteAsync(), _ => IsSelectedRunning, ReportError);
            CancelSendFilesCommand = new RelayCommand(_ => CancelSendFiles());

            _audioTimer = _dispatcher.CreateTimer();
            _audioTimer.Interval = TimeSpan.FromMilliseconds(500);
            _audioTimer.Tick += (_, _) => _ = KeepAudioAsync();
            _audioTimer.Start();

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
                RefreshLanServices(checkFirewall: false);
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
            RaisePropertyChanged(nameof(CanClone));
            RaiseSettingsProperties();

            // Sessions only become available once QEMU is found, so the rows are built after detection
            // rather than relying on the collection-changed hook alone.
            RebuildItems();

            if (!_autoStarted && Manager.IsQemuAvailable)
            {
                _autoStarted = true;
                _ = StartWithWinQemuAsync();
            }
        }

        // ---- Start with WinQEMU ----

        private bool _autoStarted;

        /// <summary>
        /// Starts the machines marked to start with WinQEMU, each after its own delay from now, as Hyper-V's
        /// automatic start does. A machine the user started (or deleted) in the meantime is left alone.
        /// </summary>
        private async Task StartWithWinQemuAsync()
        {
            var opened = DateTime.UtcNow;
            foreach (var entry in AutoStartPlan.For(Manager.VirtualMachines))
            {
                var wait = opened + entry.After - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);
                if (!Manager.VirtualMachines.Contains(entry.Machine))
                    continue;

                var session = Manager.GetSession(entry.Machine);
                if (session.State != VmState.Stopped)
                    continue;
                try
                {
                    AppendDiagnostic($"Start with WinQEMU: starting {entry.Machine.Name}.");
                    await StartMachineAsync(session);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or QmpException
                                               or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    ReportError(new InvalidOperationException(
                        $"« {entry.Machine.Name} » n'a pas pu démarrer avec WinQEMU : {ex.Message}", ex));
                }
            }
        }

        /// <summary>
        /// A machine other than the selected one came up: started with WinQEMU, or relaunched by its session
        /// when the guest rebooted. The view takes its console out of sight (VmConsoleHost.AdoptAsync); its
        /// shared folders and display are seen to as for the selected one.
        /// </summary>
        public event Action<int>? BackgroundConsoleStarted;

        private void OnAnySessionStateChanged(object? sender, VmState state)
        {
            if (state != VmState.Running || sender is not VmSession session)
                return;
            _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(session, SelectedSession) || session.ProcessId is not { } processId)
                    return;
                BackgroundConsoleStarted?.Invoke(processId);
                MountSharedFolders(session);
                PrepareGuestDisplay(session);
                CheckGuestTools(session);
            });
        }

        /// <summary>
        /// Asks every running machine's guest to shut down, as its Arrêter button does. A paused one is resumed
        /// first: frozen, it would never hear the power button.
        /// </summary>
        public async Task RequestShutdownOfAllAsync()
        {
            foreach (var session in Manager.RunningSessions.ToList())
            {
                try
                {
                    if (session.State == VmState.Paused)
                        await session.ResumeAsync();
                    await session.RequestShutdownAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or QmpException or IOException)
                {
                    AppendDiagnostic($"{session.Config.Name}: shutdown could not be asked for ({ex.Message}).");
                }
            }
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

        /// <summary>Pointer events from the console, when the shell points for QEMU.</summary>
        public async Task ForwardPointerAsync(IReadOnlyList<Core.Input.PointerEvent> pointer)
        {
            // Failures go back to the console, which traces the first few: unlike a key, a pointer that
            // does nothing has no other sign of why.
            var session = SelectedSession;
            if (session is null || session.State is not VmState.Running)
                throw new InvalidOperationException($"no running session ({session?.State.ToString() ?? "none selected"})");

            await session.SendPointerAsync(pointer);
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

        /// <summary>
        /// Cloning reads the disks with qemu-img, which cannot open an image a QEMU process holds, so the
        /// machine has to be fully down — the same rule as editing its hardware.
        /// </summary>
        public bool CanClone => SelectedVm is not null && Manager.IsQemuAvailable && !IsSelectedAlive;

        /// <summary>Clones the selected machine, fully or linked, and selects the clone.</summary>
        public async Task<VmConfig> CloneSelectedAsync(
            string name, bool linked, IProgress<double> progress, System.Threading.CancellationToken cancellationToken)
        {
            var source = SelectedVm ?? throw new InvalidOperationException("Aucune machine virtuelle n'est sélectionnée.");

            // Adding to the library rebuilds the rows; selecting the clone has to happen after that.
            var clone = linked
                ? await Manager.CreateLinkedCloneAsync(source, name, cancellationToken)
                : await Manager.CloneVirtualMachineAsync(source, name, progress, cancellationToken);
            SelectConfig(clone);
            return clone;
        }

        /// <summary>Exports the selected machine as an .ova; the same conditions as a clone (<see cref="CanClone"/>).</summary>
        public Task ExportSelectedAsync(string path, IProgress<double> progress, System.Threading.CancellationToken cancellationToken)
        {
            var source = SelectedVm ?? throw new InvalidOperationException("Aucune machine virtuelle n'est sélectionnée.");
            return Manager.ExportVirtualMachineAsync(source, path, progress, cancellationToken);
        }

        /// <summary>Imports a foreign machine and selects it.</summary>
        public async Task<VmConfig> ImportAsync(
            Core.Import.ImportPlan plan, string name, IProgress<double> progress, System.Threading.CancellationToken cancellationToken)
        {
            // Adding to the library rebuilds the rows; selecting the import has to happen after that.
            var imported = await Manager.ImportVirtualMachineAsync(plan, name, progress, cancellationToken);
            SelectConfig(imported);
            return imported;
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
            await StartMachineAsync(SelectedSession);
        }

        public sealed record AutoSnapshotInterval(int Hours, string Label);

        public IReadOnlyList<AutoSnapshotInterval> AutoSnapshotIntervals { get; } =
            AutoSnapshots.Intervals.Select(h => new AutoSnapshotInterval(h, AutoSnapshots.IntervalLabel(h))).ToList();

        /// <summary>Starts a machine, selected or not: the Démarrer button and the start with WinQEMU.</summary>
        private async Task StartMachineAsync(VmSession session)
        {
            var config = session.Config;
            await TakeAutoSnapshotAsync(config);
            session.UpdateSharedFolders(config.SharedFolders);
            await session.StartAsync();

            // Stamped here rather than in the core: this continuation is back on the UI thread, which
            // is the only thread allowed to raise PropertyChanged on a data-bound config.
            config.LastStartedUtc = DateTime.UtcNow;

            // An unattended installation's first start presses "any key" for the CD, once: pressed again
            // at a later start, Setup would begin all over.
            if (config.UnattendedSetup == UnattendedSetupState.Pending)
            {
                config.UnattendedSetup = UnattendedSetupState.Installing;
                Manager.Save(config);
                // Ubuntu's installer kernel is booted directly by QEMU: no firmware prompt to answer.
                if (GuestOsCatalog.Find(config.GuestOs)?.Family == "Windows")
                    _ = session.PressBootKeyAsync(TimeSpan.FromSeconds(20));
                AppendDiagnostic("Unattended installation: starting the installer from the ISO.");
            }

            MountSharedFolders(session);
            PrepareGuestDisplay(session);
            CheckGuestTools(session);
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

            // Refused before the selection is cleared, so the machine stays shown with the reason.
            if (deleteFiles && Manager.WhyFilesCannotBeDeleted(target) is { } refusal)
                throw new InvalidOperationException(refusal);

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

                // A machine that restarted while another was shown (an unattended installation reboots
                // several times) still gets its display set up, and its installation finished. Once per
                // QEMU run either way.
                if (session.IsRunning)
                    PrepareGuestDisplay(session);
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
                // Any machine starting or stopping decides whether sampling goes on.
                UpdateMetricsTimer();
                if (!ReferenceEquals(sender, SelectedSession))
                    return;

                RaiseStateDependentProperties();
                NotifyConsoleTargetChanged();

                // Also after the cold restart the session makes of a guest reboot: a new QEMU, unmounted.
                if (state == VmState.Running && SelectedSession is { } running)
                {
                    MountSharedFolders(running);
                    PrepareGuestDisplay(running);
                    CheckGuestTools(running);
                }

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
            RaisePropertyChanged(nameof(CanClone));
            RaisePropertyChanged(nameof(SharedFolderStatus));
            RaisePropertyChanged(nameof(HasSharedFolderStatus));
            RaiseFileTransferProperties();
            RaiseSoundProperties();
            RaiseGuestToolsProperties();
            RaisePropertyChanged(nameof(CanTypeIntoGuest));

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

        /// <summary>The last two minutes of each running machine, kept for all of them so a machine's charts are full when it is selected.</summary>
        private readonly Dictionary<VmSession, VmMetricsHistory> _histories = new();
        private readonly HashSet<VmSession> _sampling = new();
        private int _metricsTicks;

        /// <summary>The selected machine's history, for the charts; null when it is not running.</summary>
        public VmMetricsHistory? SelectedHistory =>
            SelectedSession is { IsRunning: true } session && _histories.TryGetValue(session, out var history) ? history : null;

        /// <summary>Raised after each sample, for the charts to redraw.</summary>
        public event EventHandler? MetricsSampled;

        private void UpdateMetricsTimer()
        {
            if (Manager.VirtualMachines.Any(vm => Manager.FindSession(vm)?.IsRunning == true))
            {
                if (!_metricsTimer.IsRunning)
                    _metricsTimer.Start();
            }
            else
            {
                _metricsTimer.Stop();
            }
            if (SelectedSession?.IsRunning != true)
                ResetMetrics();
            RaisePropertyChanged(nameof(SelectedHistory));
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
            // Every running machine, not only the one shown: its charts are full when it gets selected.
            var running = Manager.VirtualMachines.Select(Manager.FindSession).OfType<VmSession>().Where(s => s.IsRunning).ToList();
            foreach (var gone in _histories.Keys.Except(running).ToList())
                _histories.Remove(gone);
            var networkTick = _metricsTicks++ % 3 == 0;
            await Task.WhenAll(running.Select(session => SampleAsync(session, networkTick)));
            MetricsSampled?.Invoke(this, EventArgs.Empty);

            var selected = SelectedSession;
            if (selected is null || !selected.IsRunning || !_histories.ContainsKey(selected))
                return;
            ShowMetrics(selected, _lastMetrics.GetValueOrDefault(selected));
        }

        private readonly Dictionary<VmSession, VmMetrics> _lastMetrics = new();

        /// <summary>
        /// One sample of one machine. The network comes from the guest agent, every third second: its
        /// channel takes one client at a time, and the shared folders and the display use it too.
        /// </summary>
        private async Task SampleAsync(VmSession session, bool networkTick)
        {
            if (!_sampling.Add(session))
                return;   // the previous sample is still waiting on QEMU or the agent
            try
            {
                VmMetrics metrics;
                try
                {
                    metrics = await session.QueryMetricsAsync();
                }
                catch (Exception ex) when (ex is QmpException or InvalidOperationException or ObjectDisposedException)
                {
                    return;
                }
                var network = networkTick ? await session.QueryGuestNetworkBytesAsync() : null;

                if (!_histories.TryGetValue(session, out var history))
                    _histories[session] = history = new VmMetricsHistory();
                history.Add(DateTimeOffset.UtcNow, metrics, network);
                _lastMetrics[session] = metrics;
            }
            finally
            {
                _sampling.Remove(session);
            }
        }

        private void ShowMetrics(VmSession session, VmMetrics metrics)
        {

            var allocatedBytes = (double)session.Config.MemoryMb * 1024 * 1024;

            CpuPercent = metrics.HostCpuPercent;
            CpuText = $"CPU hôte {metrics.HostCpuPercent.ToString("0.#", CultureInfo.CurrentCulture)} %";
            RaisePropertyChanged(nameof(SelectedHistory));

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
            if (e.PropertyName is nameof(VmConfig.AudioVolume) or nameof(VmConfig.AudioMuted) or nameof(VmConfig.EnableAudio)
                && sender is VmConfig config)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    ForgetAppliedAudio(config);
                    RaiseSoundProperties();
                    _ = KeepAudioAsync();
                });
            }

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

        // ---- Access from the network ----

        /// <summary>A service of the selected guest reachable through a host forward, as one line of the section.</summary>
        public sealed class LanServiceItem
        {
            public LanServiceItem(NetworkAdapterConfig adapter, PortForward forward, IReadOnlyList<string> addresses)
            {
                Adapter = adapter;
                Forward = forward;
                Title = $"{LanAccess.LabelOf(forward)} — port {forward.HostPort.ToString(CultureInfo.InvariantCulture)} de ce PC " +
                        $"vers le port {forward.GuestPort.ToString(CultureInfo.InvariantCulture)} de l'invité";
                Usage = !LanAccess.IsOnNetwork(forward)
                    ? $"{LanAccess.UsageOf(forward, "127.0.0.1")} (depuis ce PC seulement)"
                    : addresses.Count == 0
                        ? "Ce PC n'a pas d'adresse sur un réseau local pour l'instant."
                        : string.Join("   ", addresses.Select(a => LanAccess.UsageOf(forward, a)));
            }

            public NetworkAdapterConfig Adapter { get; }
            public PortForward Forward { get; }
            public string Title { get; }
            public string Usage { get; }
        }

        public ObservableCollection<LanServiceItem> LanServices { get; } = new();

        private bool? _qemuAllowedThroughFirewall;

        public bool HasNatAdapter => SelectedVm?.NetworkAdapters.Any(a => a.Mode == NetworkMode.Nat) == true;

        public string LanAccessHint => HasNatAdapter
            ? "La machine est en NAT : elle sort sur le réseau, mais rien ne l'atteint depuis les autres " +
              "appareils, sauf les services ouverts ici. Ils s'ouvrent aussitôt si elle est en marche."
            : "Il faut une carte réseau en NAT pour ouvrir des services (section Réseau).";

        /// <summary>Said only when the firewall is known to block QEMU; nothing to say when it lets it through.</summary>
        public string? FirewallWarning => _qemuAllowedThroughFirewall == false && LanServices.Any(s => LanAccess.IsOnNetwork(s.Forward))
            ? "Le pare-feu Windows bloque QEMU : depuis le réseau, rien n'arrivera aux services ouverts tant qu'il " +
              "n'est pas autorisé (réseaux privés et de domaine seulement, pas les réseaux publics)."
            : null;

        public bool HasFirewallWarning => FirewallWarning is not null;

        /// <summary>
        /// Rebuilds the list from the selection's forwards. The firewall is asked again only when told to
        /// (the section opening): walking its rules through COM takes a noticeable moment, too long for
        /// every selection change, and the answer is the same for every machine.
        /// </summary>
        public void RefreshLanServices(bool checkFirewall = true)
        {
            LanServices.Clear();
            var target = SelectedVm;
            if (target is not null)
            {
                var addresses = LanAccess.LanAddresses();
                foreach (var adapter in target.NetworkAdapters.Where(a => a.Mode == NetworkMode.Nat))
                {
                    foreach (var forward in adapter.PortForwards.Where(f => f.HostPort > 0 && f.GuestPort > 0))
                        LanServices.Add(new LanServiceItem(adapter, forward, addresses));
                }
            }

            if (checkFirewall)
                _qemuAllowedThroughFirewall = QemuPath is { } path ? LanAccess.IsQemuAllowed(path) : null;
            RaisePropertyChanged(nameof(HasNatAdapter));
            RaisePropertyChanged(nameof(LanAccessHint));
            RaisePropertyChanged(nameof(FirewallWarning));
            RaisePropertyChanged(nameof(HasFirewallWarning));
        }

        private string? QemuPath =>
            SelectedVm is { } vm && Manager.Installation is { } installation ? installation.GetSystemBinary(vm.Architecture) : null;

        /// <summary>
        /// Opens a guest port to the network on the first NAT adapter, on a free host port; live when the
        /// machine runs. Returns the reason when nothing was opened.
        /// </summary>
        public async Task<string?> OpenServiceAsync(PortForwardProtocol protocol, int guestPort)
        {
            var target = SelectedVm;
            var adapter = target?.NetworkAdapters.FirstOrDefault(a => a.Mode == NetworkMode.Nat);
            if (target is null || adapter is null)
                return "Il faut une carte réseau en NAT pour ouvrir un service.";

            if (adapter.PortForwards.FirstOrDefault(f => f.GuestPort == guestPort && f.Protocol == protocol && LanAccess.IsOnNetwork(f)) is { } existing)
                return $"Ce service est déjà ouvert, sur le port {existing.HostPort.ToString(CultureInfo.InvariantCulture)} de ce PC.";

            // Ports other machines forward count as taken even while they are stopped: both would start.
            var taken = Manager.VirtualMachines.SelectMany(vm => vm.NetworkAdapters).SelectMany(a => a.PortForwards).Select(f => f.HostPort);
            var forward = new PortForward
            {
                Protocol = protocol,
                HostPort = LanAccess.ChooseHostPort(guestPort, taken),
                GuestPort = guestPort,
                HostAddress = LanAccess.AllInterfaces
            };

            if (SelectedSession is { IsRunning: true } session)
            {
                try
                {
                    await session.AddHostForwardAsync(adapter.Id, forward);
                }
                catch (Exception ex) when (ex is InvalidOperationException or QmpException)
                {
                    return ex.Message;
                }
            }

            adapter.PortForwards.Add(forward);
            SaveSelected();
            RefreshLanServices();
            return null;
        }

        /// <summary>
        /// Saves a card's cable state (already set by its switch), and plugs it in or out at once when the
        /// machine runs. Refused live, the switch goes back so it never shows what the guest does not see.
        /// </summary>
        public async Task SetAdapterConnectedAsync(NetworkAdapterConfig adapter)
        {
            if (SelectedSession is { IsRunning: true } session && adapter.Mode != NetworkMode.None)
            {
                try
                {
                    await session.SetLinkAsync(adapter.Id, adapter.Connected);
                }
                catch (Exception ex) when (ex is InvalidOperationException or QmpException)
                {
                    adapter.Connected = !adapter.Connected;
                    ReportError(ex);
                    return;
                }
            }
            SaveSelected();
        }

        /// <summary>Closes a service, live when the machine runs.</summary>
        public async Task CloseServiceAsync(LanServiceItem item)
        {
            if (SelectedSession is { IsRunning: true } session)
            {
                try
                {
                    await session.RemoveHostForwardAsync(item.Adapter.Id, item.Forward);
                }
                catch (Exception ex) when (ex is InvalidOperationException or QmpException)
                {
                    ReportError(ex);
                }
            }

            item.Adapter.PortForwards.Remove(item.Forward);
            SaveSelected();
            RefreshLanServices();
        }

        /// <summary>Adds the firewall rule for QEMU; one UAC prompt. Returns the reason when it was not added.</summary>
        public async Task<string?> AllowQemuThroughFirewallAsync()
        {
            if (QemuPath is not { } path)
                return null;

            try
            {
                await LanAccess.AllowQemuAsync(path);
            }
            catch (OperationCanceledException)
            {
                return "Le pare-feu n'a pas été modifié : l'autorisation administrateur a été refusée.";
            }

            RefreshLanServices();
            return _qemuAllowedThroughFirewall == false
                ? "La règle n'a pas pris effet. Une stratégie d'entreprise interdit peut-être les règles de pare-feu locales."
                : null;
        }

        // ---- Shared folders ----

        /// <summary>Why a name cannot be given to the selection, or null when it can.</summary>
        public string? WhyNameIsRefused(string name) =>
            VmLibrary.ValidateName(name)
            ?? (Manager.VirtualMachines.Any(vm => !ReferenceEquals(vm, SelectedVm)
                                                  && string.Equals(vm.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase))
                ? "Une autre machine porte déjà ce nom."
                : null);

        public void RenameSelected(string name)
        {
            if (SelectedVm is not { } target || WhyNameIsRefused(name) is not null)
                return;

            target.Name = name.Trim();
            SaveSelected();
        }

        /// <summary>The networks a card can be plugged into, for the Réseau section's picker.</summary>
        public ObservableCollection<VirtualNetwork> VirtualNetworks => Manager.Settings.VirtualNetworks;

        public string SharedFolderHint =>
            "Comme les dossiers partagés de VMware : WinQEMU les sert lui-même par un canal privé, sans réseau " +
            $"ni mot de passe, et ils apparaissent dans ~/{GuestShareMounter.GuestFolder} d'un invité Linux. Il y " +
            "faut une seule fois l'agent invité : sudo apt install qemu-guest-agent. Les invités Windows ne sont " +
            "pas encore pris en charge.";

        private readonly Dictionary<VmConfig, GuestShareReport> _shareReports = new();

        // ---- Windows guest resolution (see WindowsGuestDisplay) ----

        /// <summary>QEMU runs whose Windows guest has the resolution helper set up, by agent port.</summary>
        private readonly HashSet<(VmSession Session, int Port)> _displayRuns = new();
        private readonly HashSet<(VmSession Session, int Port)> _displayReady = new();
        private (int Width, int Height) _consoleSize;
        private int _resolutionRequest;

        /// <summary>
        /// Sets up the resolution helper in a Windows guest, once per QEMU process, then asks for the
        /// console's size. Linux guests follow the window by themselves.
        /// </summary>
        private void PrepareGuestDisplay(VmSession session)
        {
            if (session.GuestAgentPort is not { } port || !_displayRuns.Add((session, port)))
                return;
            if (GuestOsCatalog.Find(session.Config.GuestOs)?.Family == "Windows")
                _ = PrepareGuestDisplayAsync(session, port);
            else if (session.Config.UnattendedSetup == UnattendedSetupState.Installing)
                _ = FinishWhenAgentAnswersAsync(session);
        }

        /// <summary>A Linux guest follows the window by itself; only an unattended installation waits on its agent.</summary>
        private async Task FinishWhenAgentAnswersAsync(VmSession session)
        {
            try
            {
                // The installer reboots into the installed system, which installs the agent on first boot: long.
                await using var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromMinutes(60));
                if (agent is not null)
                    FinishUnattendedSetup(session.Config);
            }
            catch (Exception ex) when (ex is GuestAgentException or IOException or OperationCanceledException or InvalidOperationException)
            {
                AppendDiagnostic($"Unattended installation: the guest agent did not answer ({ex.Message}).");
            }
        }

        private async Task PrepareGuestDisplayAsync(VmSession session, int port)
        {
            // As for the shared folders: the agent answers before Windows can start programs.
            for (var attempt = 1; attempt <= 5 && session.IsRunning && session.GuestAgentPort == port; attempt++)
            {
                try
                {
                    await using var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromMinutes(5));
                    if (agent is null)
                        return;
                    FinishUnattendedSetup(session.Config);
                    await WindowsGuestDisplay.SetupAsync(agent);
                    _displayReady.Add((session, port));
                    AppendDiagnostic("Display: the guest's resolution now follows the console.");
                    RequestGuestResolution(_consoleSize.Width, _consoleSize.Height);
                    return;
                }
                catch (Exception ex) when (ex is GuestAgentException or IOException or OperationCanceledException or InvalidOperationException)
                {
                    if (attempt == 5)
                        AppendDiagnostic($"Display: the guest's resolution will not follow the console ({ex.Message}).");
                    else
                        await Task.Delay(TimeSpan.FromSeconds(20));
                }
            }
        }

        /// <summary>
        /// An unattended installation is over once the guest agent, installed at the first logon, answers:
        /// the answer file goes (it holds the password, base64 only), and the machine stops starting from the
        /// ISO, whose prompt would otherwise greet every start.
        /// </summary>
        private void FinishUnattendedSetup(VmConfig config)
        {
            if (config.UnattendedSetup != UnattendedSetupState.Installing)
                return;

            var folder = QemuArgumentBuilder.GetWindowsSetupFolder(config);
            foreach (var file in new[]
                     {
                         WindowsSetupAnswer.FileName, WindowsUnattendedSetup.ScriptFileName, WindowsUnattendedSetup.AccountScriptFileName,
                         UbuntuUnattendedSetup.KernelFile, UbuntuUnattendedSetup.InitrdFile,
                         Path.Combine(UbuntuUnattendedSetup.CidataFolder, "user-data"), Path.Combine(UbuntuUnattendedSetup.CidataFolder, "meta-data")
                     })
            {
                try
                {
                    File.Delete(Path.Combine(folder, file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppendDiagnostic($"Unattended installation: {file} could not be deleted ({ex.Message}).");
                }
            }
            config.BootFromIso = false;
            config.UnattendedSetup = UnattendedSetupState.None;
            Manager.Save(config);
            AppendDiagnostic("Unattended installation: done. Answer file deleted; the machine now starts from its disk.");
        }

        /// <summary>
        /// The console's size in physical pixels, from "Ajuster" or full screen. Asked of the guest once
        /// it has stopped changing for a moment: dragging the window's edge reports dozens of sizes.
        /// </summary>
        public void RequestGuestResolution(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;
            _consoleSize = (width, height);
            var request = ++_resolutionRequest;
            _ = SendResolutionAsync(request, width, height);
        }

        private async Task SendResolutionAsync(int request, int width, int height)
        {
            await Task.Delay(800);
            if (request != _resolutionRequest || SelectedSession is not { IsRunning: true } session
                || session.GuestAgentPort is not { } port || !_displayReady.Contains((session, port)))
                return;

            try
            {
                await using var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromSeconds(10));
                if (agent is not null)
                    await WindowsGuestDisplay.RequestAsync(agent, width, height);
            }
            catch (Exception ex) when (ex is GuestAgentException or IOException or OperationCanceledException or InvalidOperationException)
            {
                AppendDiagnostic($"Display: {width}x{height} could not be asked of the guest ({ex.Message}).");
            }
        }
        private readonly HashSet<(VmSession Session, int Port)> _mountRuns = new();

        /// <summary>Where the selection's folders stand inside the running guest, or null when it is not running.</summary>
        public string? SharedFolderStatus =>
            SelectedVm is { } vm && IsSelectedRunning && _shareReports.TryGetValue(vm, out var report) ? report.Text : null;

        public bool HasSharedFolderStatus => SharedFolderStatus is not null;

        /// <summary>
        /// Mounts the folders in the guest, once per QEMU process: the session relaunches QEMU on a guest
        /// reboot, and the new one starts with nothing mounted. Once mounted, folders shared later appear
        /// under the mount by themselves.
        /// </summary>
        private void MountSharedFolders(VmSession session)
        {
            if (session.Config.SharedFolders.Count == 0 || session.GuestAgentPort is not { } port || !_mountRuns.Add((session, port)))
                return;

            _ = MountSharedFoldersAsync(session);
        }

        private async Task MountSharedFoldersAsync(VmSession session)
        {
            var config = session.Config;
            SetShareReport(config, new GuestShareReport(GuestShareState.WaitingForAgent,
                "En attente de l'agent invité pour monter les dossiers…"));

            GuestShareReport report;
            try
            {
                // Five minutes: a first boot, or one that runs a disk check, can take that long.
                var hasNatCard = config.NetworkAdapters.Any(a => a.Mode == NetworkMode.Nat && a.Connected);
                var windowsGuest = GuestOsCatalog.Find(config.GuestOs)?.Family == "Windows";
                report = await GuestShareMounter.MountAsync(session, TimeSpan.FromMinutes(5), hasNatCard, windowsGuest);
            }
            catch (Exception ex) when (ex is GuestAgentException or IOException or OperationCanceledException)
            {
                report = new GuestShareReport(GuestShareState.Failed, $"Les dossiers n'ont pas pu être montés : {ex.Message}");
            }

            if (session.IsRunning)
                SetShareReport(config, report);
        }

        private void SetShareReport(VmConfig config, GuestShareReport report)
        {
            _shareReports[config] = report;
            RaisePropertyChanged(nameof(SharedFolderStatus));
            RaisePropertyChanged(nameof(HasSharedFolderStatus));
        }

        /// <summary>Hands the selection's current folders to its running session, which serves them live.</summary>
        private void PublishSharedFolders(VmConfig config)
        {
            var session = Manager.FindSession(config);
            session?.UpdateSharedFolders(config.SharedFolders);
            if (session is { IsRunning: true })
                MountSharedFolders(session);
        }

        /// <summary>Shares a host folder with the selected machine. Returns the reason when it was not.</summary>
        public string? ShareFolder(string hostPath, bool readOnly)
        {
            var target = SelectedVm;
            if (target is null)
                return null;

            if (target.SharedFolders.Any(f => SamePath(f.HostPath, hostPath)))
                return "Ce dossier est déjà partagé avec cette machine.";

            var stem = SharedFolderConfig.DefaultNameFor(hostPath);
            var name = stem;
            for (var index = 2; target.SharedFolders.Any(f => string.Equals(f.GuestName, name, StringComparison.OrdinalIgnoreCase)); index++)
                name = $"{stem} ({index.ToString(CultureInfo.InvariantCulture)})";

            target.SharedFolders.Add(new SharedFolderConfig { HostPath = hostPath, Name = name, ReadOnly = readOnly });
            SaveSelected();
            PublishSharedFolders(target);
            AppendDiagnostic($"Dossier partagé : {hostPath} → ~/{GuestShareMounter.GuestFolder}/{name}");
            return null;
        }

        /// <summary>Stops sharing a folder; a running guest sees it disappear from ~/Partages.</summary>
        public void UnshareFolder(SharedFolderConfig folder)
        {
            var target = SelectedVm;
            if (target is null || !target.SharedFolders.Remove(folder))
                return;

            SaveSelected();
            PublishSharedFolders(target);
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                          Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        // ---- USB devices ----

        /// <summary>One line of the USB menu: a host device, or one recorded for every start and unplugged.</summary>
        public sealed record UsbMenuEntry(
            int VendorId,
            int ProductId,
            string Name,
            Core.Usb.HostUsbDevice? Host,
            bool Connected,
            bool Waiting,
            bool AtStart)
        {
            public string IdText => UsbDeviceConfig.FormatId(VendorId, ProductId);
        }

        /// <summary>
        /// What the USB menu shows for the selection: the devices plugged into the host, which of them the
        /// running machine holds, and which are recorded for every start, plugged in or not.
        /// </summary>
        public async Task<IReadOnlyList<UsbMenuEntry>> GetUsbEntriesAsync()
        {
            var target = SelectedVm;
            if (target is null)
                return Array.Empty<UsbMenuEntry>();

            var host = await Task.Run(Core.Usb.HostUsbEnumerator.List);

            IReadOnlyList<(int VendorId, int ProductId, bool Attached)> inGuest = Array.Empty<(int, int, bool)>();
            if (SelectedSession is { IsRunning: true } session)
            {
                try
                {
                    inGuest = await session.QueryUsbDevicesAsync();
                }
                catch (Exception ex)
                {
                    ReportError(ex);
                }
            }

            UsbMenuEntry Entry(int vendor, int product, string name, Core.Usb.HostUsbDevice? device)
            {
                var held = inGuest.FirstOrDefault(d => d.VendorId == vendor && d.ProductId == product);
                var present = held != default;
                return new UsbMenuEntry(vendor, product, name, device,
                    Connected: present && held.Attached,
                    Waiting: present && !held.Attached,
                    AtStart: target.UsbDevices.Any(d => d.Matches(vendor, product)));
            }

            var entries = host
                .GroupBy(d => (d.VendorId, d.ProductId))
                .Select(g => Entry(g.Key.VendorId, g.Key.ProductId, g.First().Name, g.First()))
                .ToList();
            entries.AddRange(target.UsbDevices
                .Where(d => !host.Any(h => d.Matches(h.VendorId, h.ProductId)))
                .DistinctBy(d => (d.VendorId, d.ProductId))
                .Select(d => Entry(d.VendorId, d.ProductId, d.Name.Length > 0 ? d.Name : "Périphérique USB", null)));
            return entries;
        }

        /// <summary>
        /// Connects or disconnects a device on the running machine. Returns the reason when QEMU could
        /// not take it, worded from the driver Windows has on it.
        /// </summary>
        public async Task<string?> ToggleUsbAsync(UsbMenuEntry entry)
        {
            if (SelectedSession is not { IsRunning: true } session)
                return null;

            try
            {
                if (entry.Connected)
                {
                    await session.DisconnectUsbAsync(entry.VendorId, entry.ProductId);
                    AppendDiagnostic($"USB : {entry.Name} rendu à Windows.");
                    return null;
                }

                if (await session.ConnectUsbAsync(entry.VendorId, entry.ProductId))
                {
                    AppendDiagnostic($"USB : {entry.Name} connecté à la machine.");
                    return null;
                }

                return entry.Host is { IsReachable: false } device
                    ? $"{entry.Name} utilise le pilote « {device.Service} », que QEMU ne sait pas ouvrir : il lui faut le " +
                      "pilote WinUSB. Le menu USB explique comment l'installer."
                    : entry.Host is null
                        ? $"{entry.Name} n'est pas branché."
                        : $"QEMU n'a pas pu ouvrir {entry.Name} : un autre programme s'en sert sans doute (Windows Hello " +
                          "pour un lecteur d'empreintes, un logiciel du fabricant…), ou il est déjà donné à une autre machine.";
            }
            catch (Exception ex)
            {
                ReportError(ex);
                return null;
            }
        }

        /// <summary>
        /// Records or forgets a device for every start of the selection. Takes effect at the next start;
        /// the running machine is left as it is.
        /// </summary>
        public void SetUsbAtStart(UsbMenuEntry entry, bool atStart)
        {
            var target = SelectedVm;
            if (target is null)
                return;

            foreach (var stale in target.UsbDevices.Where(d => d.Matches(entry.VendorId, entry.ProductId)).ToList())
                target.UsbDevices.Remove(stale);
            if (atStart)
                target.UsbDevices.Add(new UsbDeviceConfig { VendorId = entry.VendorId, ProductId = entry.ProductId, Name = entry.Name });

            SaveSelected();
            AppendDiagnostic(atStart
                ? $"USB : {entry.Name} sera connecté à chaque démarrage."
                : $"USB : {entry.Name} ne sera plus connecté au démarrage.");
        }

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

        /// <summary>
        /// The automatic snapshot, if one is due, while the machine is still stopped (Services.AutoSnapshots).
        /// A failure is said but does not keep the machine from starting.
        /// </summary>
        private async Task TakeAutoSnapshotAsync(VmConfig config)
        {
            if (Manager.DiskService is not { } images || !AutoSnapshots.IsDue(config, DateTime.UtcNow))
                return;
            try
            {
                var name = await AutoSnapshots.TakeIfDueAsync(config, images, Manager.VirtualMachines.ToList(), DateTime.UtcNow);
                Manager.Save(config);
                if (name is not null)
                    AppendDiagnostic($"Automatic snapshot: {name}.");
            }
            catch (Exception ex) when (ex is QemuImgException or IOException or InvalidOperationException)
            {
                ReportError(new InvalidOperationException(
                    $"L'instantané automatique de « {config.Name} » n'a pas pu être pris : {ex.Message}", ex));
            }
        }

        /// <summary>
        /// Has the guest say which blocks of its disks are free (TRIM, through the agent), so that compacting the
        /// disk afterwards gives that space back. Returns what to tell the user.
        /// </summary>
        public async Task<string> TrimGuestDisksAsync()
        {
            if (SelectedVm is not { } target || SelectedSession is not { IsRunning: true } session)
                return "La machine doit être en marche.";
            var withoutDiscard = target.Disks.Where(d => !d.Discard).Select(d => d.Id).ToList();
            try
            {
                await using var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromSeconds(15));
                if (agent is null)
                    return "L'agent invité ne répond pas : installez d'abord les outils invités.";
                var count = await DiskCompaction.TrimAsync(agent);
                AppendDiagnostic($"TRIM: {count} filesystem(s) trimmed in the guest.");
                var text = $"{count} système(s) de fichiers de l'invité ont signalé leur espace libre. Arrêtez la machine, puis " +
                           "« Compacter… » chaque disque (Matériel) pour rendre cette place à ce PC.";
                if (withoutDiscard.Count > 0)
                {
                    text += $"\n\nLe ou les disques {string.Join(", ", withoutDiscard)} n'ont pas l'option « Discard » : l'espace libre " +
                            "n'y atteint pas l'image. Cochez-la dans Matériel (machine arrêtée) avant de recommencer.";
                }
                return text;
            }
            catch (Exception ex) when (ex is GuestAgentException or IOException or InvalidOperationException)
            {
                return $"L'invité n'a pas pu signaler son espace libre : {ex.Message}";
            }
        }

        public bool CanTrimGuestDisks => IsSelectedRunning && GuestToolsState is GuestToolsState.Complete or GuestToolsState.Incomplete;

        // ---- Typing into the guest ----

        private bool _typing;

        /// <summary>The selection can be typed into: running, and not typing already.</summary>
        public bool CanTypeIntoGuest => IsSelectedRunning && !_typing;

        /// <summary>
        /// Types text at the selection's keyboard, in this PC's layout (Input.KeyboardText): the clipboard of a
        /// guest that shares none, a text console or a login screen. Every character is checked before the first
        /// key goes, so a text is typed whole or not at all. Returns what went wrong, or null.
        /// </summary>
        public async Task<string?> TypeIntoGuestAsync(string text)
        {
            if (SelectedSession is not { IsRunning: true } session || _typing)
                return "La machine doit être en marche.";
            IReadOnlyList<IReadOnlyList<(int Number, bool Down)>> strokes;
            try
            {
                strokes = Core.Input.KeyboardText.ToKeystrokes(text, Core.Input.KeyboardText.CurrentLayout());
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }

            _typing = true;
            RaisePropertyChanged(nameof(CanTypeIntoGuest));
            try
            {
                await session.TypeAsync(strokes);
                AppendDiagnostic($"Keyboard: {text.Length} character(s) typed in the guest.");
                return null;
            }
            catch (Exception ex) when (ex is QmpException or InvalidOperationException or IOException)
            {
                return $"La saisie s'est interrompue : {ex.Message}";
            }
            finally
            {
                _typing = false;
                RaisePropertyChanged(nameof(CanTypeIntoGuest));
            }
        }

        // ---- Guest tools ----

        private readonly Dictionary<VmConfig, GuestToolsReport> _toolsReports = new();
        private readonly HashSet<(VmSession Session, int Port)> _toolsRuns = new();
        private readonly HashSet<VmConfig> _toolsInstalls = new();

        /// <summary>
        /// Looks for the guest's tools once per QEMU process (a guest reboot starts a new one): waits a few
        /// minutes for the agent, then asks it what else is there. Not during an installation.
        /// </summary>
        private void CheckGuestTools(VmSession session)
        {
            if (!GuestTools.ShouldCheck(session.Config) || session.GuestAgentPort is not { } port || !_toolsRuns.Add((session, port)))
                return;
            _ = CheckGuestToolsAsync(session, GuestTools.AgentWait);
        }

        private async Task CheckGuestToolsAsync(VmSession session, TimeSpan agentWait)
        {
            var config = session.Config;
            SetToolsReport(config, new GuestToolsReport(GuestToolsState.Checking, "Recherche de l'agent invité…"));
            GuestToolsReport report;
            try
            {
                await using var agent = await GuestShareMounter.WaitForAgentAsync(session, agentWait);
                report = agent is null
                    ? new GuestToolsReport(GuestToolsState.AgentMissing,
                        "Les outils invités ne sont pas installés : dossiers partagés, glisser-déposer, presse-papiers et taille " +
                        "d'écran automatique en dépendent.")
                    : await GuestTools.CheckAsync(agent, GuestTools.IsWindows(config));
            }
            catch (Exception ex) when (ex is GuestAgentException or IOException or OperationCanceledException or InvalidOperationException)
            {
                report = new GuestToolsReport(GuestToolsState.Unknown, string.Empty);
            }
            if (session.IsRunning && !_toolsInstalls.Contains(config))
                SetToolsReport(config, report);
        }

        private void SetToolsReport(VmConfig config, GuestToolsReport report)
        {
            _toolsReports[config] = report;
            RaiseGuestToolsProperties();
        }

        private GuestToolsReport? SelectedToolsReport =>
            SelectedVm is { } vm && IsSelectedRunning && _toolsReports.TryGetValue(vm, out var report) ? report : null;

        public GuestToolsState GuestToolsState => SelectedToolsReport?.State ?? GuestToolsState.Unknown;

        /// <summary>Where the selection's tools stand, for the summary; null while unknown.</summary>
        public string? GuestToolsText => SelectedToolsReport is { State: not GuestToolsState.Unknown } report ? report.Text : null;

        public bool HasGuestToolsText => GuestToolsText is not null;

        public bool IsInstallingGuestTools => SelectedVm is { } vm && _toolsInstalls.Contains(vm);

        public bool CanInstallGuestTools =>
            !IsInstallingGuestTools && GuestToolsState is GuestToolsState.AgentMissing or GuestToolsState.Incomplete;

        /// <summary>Said in the console bar too: missing tools, or their installation under way.</summary>
        public bool HasGuestToolsWarning => CanInstallGuestTools || IsInstallingGuestTools;

        public bool ShowGuestToolsWarning => HasGuestToolsWarning && !HasSendFilesStatus;

        public bool ShowConsoleHint => !HasSendFilesStatus && !HasGuestToolsWarning;

        public string GuestToolsWarning => IsInstallingGuestTools
            ? GuestToolsText ?? "Installation des outils invités…"
            : "Outils invités absents ou incomplets.";

        public bool IsSelectedWindowsGuest => SelectedVm is { } vm && GuestTools.IsWindows(vm);

        private void RaiseGuestToolsProperties()
        {
            RaisePropertyChanged(nameof(GuestToolsState));
            RaisePropertyChanged(nameof(GuestToolsText));
            RaisePropertyChanged(nameof(HasGuestToolsText));
            RaisePropertyChanged(nameof(IsInstallingGuestTools));
            RaisePropertyChanged(nameof(CanInstallGuestTools));
            RaisePropertyChanged(nameof(HasGuestToolsWarning));
            RaisePropertyChanged(nameof(ShowGuestToolsWarning));
            RaisePropertyChanged(nameof(ShowConsoleHint));
            RaisePropertyChanged(nameof(GuestToolsWarning));
            RaisePropertyChanged(nameof(CanTrimGuestDisks));
        }

        /// <summary>virtio-win's ISO if one can be found without asking (GuestTools.FindVirtioWinIso).</summary>
        public string? FindVirtioWinIso() => GuestTools.FindVirtioWinIso(Settings.VirtioWinIsoPath, Manager.VirtualMachines);

        public void RememberVirtioWinIso(string path)
        {
            Settings.VirtioWinIsoPath = path;
            Settings.Save();
        }

        /// <summary>
        /// Installs the selection's tools: types the first step at the guest's keyboard when there is no agent
        /// (see GuestTools), waits for the agent, then completes through it. Windows needs virtio-win's ISO,
        /// put in the machine's drive. Returns what went wrong, or null.
        /// </summary>
        public async Task<string?> InstallGuestToolsAsync(string? virtioWinIso)
        {
            if (SelectedVm is not { } target || SelectedSession is not { IsRunning: true } session)
                return "La machine doit être en marche.";
            var windows = GuestTools.IsWindows(target);
            var agentMissing = GuestToolsState == GuestToolsState.AgentMissing;
            var layout = Core.Input.KeyboardText.CurrentLayout();
            _toolsInstalls.Add(target);
            void Say(string text) => SetToolsReport(target, new GuestToolsReport(GuestToolsState.Checking, text));
            try
            {
                if (windows)
                {
                    if (virtioWinIso is null)
                        return "L'ISO virtio-win est nécessaire pour un invité Windows.";
                    Say("Insertion de l'ISO virtio-win…");
                    await ChangeIsoAsync(virtioWinIso);
                    // Time for Windows to see the new disc and give it a letter.
                    await Task.Delay(TimeSpan.FromSeconds(6));
                }

                if (agentMissing)
                {
                    if (windows)
                    {
                        // Started by the user from the drive: typed in the Run box, Defender takes it for the
                        // "ClickFix" attack and stops it (see GuestTools).
                        Say("Dans l'invité, lancez virtio-win-guest-tools.exe puis guest-agent\\qemu-ga-x86_64.msi depuis le lecteur virtio-win ; WinQEMU attend l'agent…");
                    }
                    else
                    {
                        Say("Saisie de la commande dans l'invité…");
                        await session.TypeAsync(Core.Input.KeyboardText.ToKeystrokes(GuestTools.LinuxInstallCommand + "\n", layout));
                        Say("Tapez votre mot de passe dans le terminal de l'invité si sudo le demande ; l'installation suit…");
                        AppendDiagnostic("Guest tools: installation command typed in the guest.");
                    }

                    await using var answered = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromMinutes(windows ? 20 : 10));
                    if (answered is null)
                    {
                        return windows
                            ? "L'agent invité ne répond toujours pas. Vérifiez dans l'invité que les deux installations sont allées à leur terme."
                            : "L'agent invité ne répond toujours pas après dix minutes. Vérifiez dans l'invité que la commande s'est bien exécutée.";
                    }
                }

                Say("Installation des outils par l'agent invité…");
                string? note = null;
                await using (var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromMinutes(1)))
                {
                    if (agent is null)
                        return "L'agent invité a cessé de répondre.";
                    if ((await GuestTools.CheckAsync(agent, windows)).State != GuestToolsState.Complete)
                    {
                        var result = await GuestTools.CompleteAsync(agent, windows);
                        if (result.ExitCode is not (0 or 3010))
                            return $"Les outils n'ont pas pu être installés : {GuestTools.Explain(result)}.";
                        note = result.ExitCode == 3010
                            ? "Redémarrez l'invité pour terminer l'installation des pilotes."
                            : windows ? null : "Rouvrez la session de l'invité pour que le presse-papiers soit partagé.";
                    }
                }

                // What waited for the agent and gave up can try again.
                if (session.GuestAgentPort is { } port)
                {
                    _mountRuns.Remove((session, port));
                    MountSharedFolders(session);
                    PrepareGuestDisplay(session);
                }
                AppendDiagnostic("Guest tools: installed.");
                _toolsInstalls.Remove(target);
                SetToolsReport(target, new GuestToolsReport(GuestToolsState.Complete,
                    note is null ? "Outils invités installés." : $"Outils invités installés. {note}"));
                return null;
            }
            catch (Exception ex) when (ex is GuestAgentException or IOException or QmpException or InvalidOperationException
                                           or ArgumentException or OperationCanceledException)
            {
                return ex.Message;
            }
            finally
            {
                if (_toolsInstalls.Remove(target) && session.IsRunning)
                {
                    // Failed: look again, so the state shown is the guest's, not the attempt's.
                    if (session.GuestAgentPort is { } port)
                        _toolsRuns.Remove((session, port));
                    _ = CheckGuestToolsAsync(session, TimeSpan.FromSeconds(10));
                }
            }
        }

        // ---- Sound ----

        /// <summary>What has been laid on one running machine's audio: its sessions, and the output it was sent to.</summary>
        private sealed class AudioRun
        {
            public int ProcessId;
            public bool Routed;
            public HashSet<string> Applied { get; } = new();
        }

        private readonly Dictionary<VmConfig, AudioRun> _audioRuns = new();
        private bool _audioBusy;

        /// <summary>The selection's sound can be cut or turned down: it has a sound card.</summary>
        public bool CanAdjustSound => SelectedVm?.EnableAudio == true;

        public string SoundLabel => SelectedVm is { EnableAudio: true, AudioMuted: true } ? "Son coupé" : "Son";

        /// <summary>Segoe Fluent's speaker, crossed out when the sound is cut.</summary>
        public string SoundGlyph => SelectedVm is { EnableAudio: true, AudioMuted: true } ? "\uE74F" : "\uE767";

        private void RaiseSoundProperties()
        {
            RaisePropertyChanged(nameof(CanAdjustSound));
            RaisePropertyChanged(nameof(SoundLabel));
            RaisePropertyChanged(nameof(SoundGlyph));
        }

        /// <summary>
        /// Every half second, lays each running machine's own volume and mute on the audio sessions its QEMU
        /// has opened (one appears when the guest first plays), and sends its sound to the chosen output.
        /// Off the UI thread: the audio API enumerates every output and session.
        /// </summary>
        /// <remarks>
        /// Laid on every new session, the machine's setting at 100 % and unmuted included: Windows gives a new
        /// session of qemu-system-x86_64.exe whatever was last set on another, so a machine started after a
        /// muted one started muted.
        /// </remarks>
        private async Task KeepAudioAsync()
        {
            if (_audioBusy)
                return;
            _audioBusy = true;
            try
            {
                var outputId = Settings.AudioOutputId;
                var work = new List<(AudioRun Run, float Level, bool Muted, bool Route)>();
                foreach (var session in Manager.RunningSessions)
                {
                    var config = session.Config;
                    if (!config.EnableAudio || session.ProcessId is not { } pid)
                        continue;
                    if (!_audioRuns.TryGetValue(config, out var run) || run.ProcessId != pid)
                        _audioRuns[config] = run = new AudioRun { ProcessId = pid };
                    work.Add((run, config.AudioVolume / 100f, config.AudioMuted, !run.Routed));
                    run.Routed = true;
                }
                foreach (var gone in _audioRuns.Keys.Where(c => Manager.FindSession(c) is not { IsRunning: true }).ToList())
                    _audioRuns.Remove(gone);
                if (work.Count == 0)
                    return;

                var unrouted = await Task.Run(() =>
                {
                    var failed = false;
                    foreach (var (run, level, muted, route) in work)
                    {
                        // Before the guest's first sound, which opens the session on whichever output is chosen.
                        if (route && !Core.Interop.HostAudio.RouteProgramOutput(run.ProcessId, outputId))
                            failed = true;
                        lock (run.Applied)
                            Core.Interop.HostAudio.SetProcessVolume(run.ProcessId, level, muted, run.Applied);
                    }
                    return failed;
                });
                if (unrouted && !string.IsNullOrEmpty(outputId))
                    AppendDiagnostic("Sound: Windows did not let WinQEMU choose the machines' audio output; they use its default one.");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                AppendDiagnostic($"Sound: the machine's volume could not be set ({ex.Message}).");
            }
            finally
            {
                _audioBusy = false;
            }
        }

        /// <summary>The machine's volume or mute changed: laid again on its sessions at the next pass.</summary>
        private void ForgetAppliedAudio(VmConfig config)
        {
            if (_audioRuns.TryGetValue(config, out var run))
            {
                lock (run.Applied)
                    run.Applied.Clear();
            }
        }

        /// <summary>A choice of output for the settings: Windows' default first, then each active output.</summary>
        public sealed record AudioOutputChoice(string Id, string Name)
        {
            public override string ToString() => Name;
        }

        public IReadOnlyList<AudioOutputChoice> AudioOutputChoices
        {
            get
            {
                var choices = new List<AudioOutputChoice> { new(string.Empty, "Sortie par défaut de Windows") };
                try
                {
                    choices.AddRange(Core.Interop.HostAudio.ListOutputs().Select(o => new AudioOutputChoice(o.Id, o.Name)));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
                {
                    AppendDiagnostic($"Sound: this PC's outputs could not be listed ({ex.Message}).");
                }
                // A headset chosen earlier and unplugged now stays chosen: Windows sends the sound to it again
                // when it comes back.
                if (Settings.AudioOutputId.Length > 0 && choices.All(c => c.Id != Settings.AudioOutputId))
                    choices.Add(new AudioOutputChoice(Settings.AudioOutputId, "Sortie choisie (débranchée)"));
                return choices;
            }
        }

        public string SelectedAudioOutputId
        {
            get => Settings.AudioOutputId;
            set
            {
                if ((value ?? string.Empty) == Settings.AudioOutputId)
                    return;
                Settings.AudioOutputId = value ?? string.Empty;
                Settings.Save();
                RaisePropertyChanged();
                // Machines already running move to it at once (the choice is the program's, so one call
                // would do; each is asked again on the next pass anyway).
                foreach (var run in _audioRuns.Values)
                    run.Routed = false;
                _ = KeepAudioAsync();
            }
        }

        // ---- Files sent to the guest ----

        /// <summary>A copy under way to one machine; the console bar shows the selection's.</summary>
        private sealed class FileTransfer
        {
            public System.Threading.CancellationTokenSource Cancel { get; } = new();
            public long Sent;
            public long Total;
            /// <summary>What the console bar says of it: "Copie vers le Bureau de l'invité", "Copie depuis l'invité".</summary>
            public string Label = "Copie vers le Bureau de l'invité";
        }

        private readonly Dictionary<VmConfig, FileTransfer> _transfers = new();
        private readonly Dictionary<VmConfig, string> _transferOutcomes = new();

        public RelayCommand CancelSendFilesCommand { get; }

        /// <summary>Files can be dropped on, or sent to, the selection: it runs, and is not already receiving some.</summary>
        public bool CanSendFiles => SelectedVm is { } vm && IsSelectedRunning && !_transfers.ContainsKey(vm);

        public bool IsSendingFiles => SelectedVm is { } vm && _transfers.ContainsKey(vm);

        public double SendFilesPercent =>
            SelectedVm is { } vm && _transfers.TryGetValue(vm, out var transfer) && transfer.Total > 0
                ? 100.0 * transfer.Sent / transfer.Total
                : 0;

        /// <summary>The copy under way, or how the last one ended, for the console bar; null when there is neither.</summary>
        public string? SendFilesStatus
        {
            get
            {
                if (SelectedVm is not { } vm)
                    return null;
                if (_transfers.TryGetValue(vm, out var transfer))
                {
                    return transfer.Total == 0
                        ? $"{transfer.Label}…"
                        : $"{transfer.Label} : {FormatBytes(transfer.Sent)} sur {FormatBytes(transfer.Total)}";
                }
                return IsSelectedRunning && _transferOutcomes.TryGetValue(vm, out var outcome) ? outcome : null;
            }
        }

        public bool HasSendFilesStatus => SendFilesStatus is not null;

        public bool HasNoSendFilesStatus => !HasSendFilesStatus;

        private void RaiseFileTransferProperties()
        {
            RaisePropertyChanged(nameof(CanSendFiles));
            RaisePropertyChanged(nameof(IsSendingFiles));
            RaisePropertyChanged(nameof(SendFilesPercent));
            RaisePropertyChanged(nameof(SendFilesStatus));
            RaisePropertyChanged(nameof(HasSendFilesStatus));
            RaisePropertyChanged(nameof(HasNoSendFilesStatus));
            RaisePropertyChanged(nameof(ShowGuestToolsWarning));
            RaisePropertyChanged(nameof(ShowConsoleHint));
        }

        /// <summary>
        /// Copies files and folders of this PC onto the desktop of the selection's guest user, as VMware's
        /// drag and drop does. Failures are thrown for the view to show; a cancel just ends the copy.
        /// </summary>
        public async Task SendFilesAsync(IReadOnlyList<string> paths)
        {
            if (SelectedVm is not { } target || SelectedSession is not { IsRunning: true } session)
                throw new InvalidOperationException("La machine doit être en marche pour recevoir des fichiers.");
            if (_transfers.ContainsKey(target))
                throw new InvalidOperationException("Une copie vers cette machine est déjà en cours : attendez qu'elle se termine.");

            var transfer = new FileTransfer();
            _transfers[target] = transfer;
            _transferOutcomes.Remove(target);
            RaiseFileTransferProperties();
            var windowsGuest = GuestOsCatalog.Find(target.GuestOs)?.Family == "Windows";
            try
            {
                // Off the UI thread: a large folder takes a while to walk.
                var entries = await Task.Run(() => GuestFileDrop.Collect(paths));
                transfer.Total = entries.Where(e => !e.IsDirectory).Sum(e => e.Length);
                RaiseFileTransferProperties();

                var progress = new Progress<DropProgress>(p =>
                {
                    transfer.Sent = p.BytesSent;
                    if (ReferenceEquals(SelectedVm, target))
                    {
                        RaisePropertyChanged(nameof(SendFilesPercent));
                        RaisePropertyChanged(nameof(SendFilesStatus));
                    }
                });
                var result = await GuestFileDrop.SendAsync(session, entries, windowsGuest, progress, transfer.Cancel.Token);

                var names = result.Names.Count <= 3
                    ? string.Join(", ", result.Names)
                    : $"{string.Join(", ", result.Names.Take(2))} et {result.Names.Count - 2} autres";
                SetTransferOutcome(target, $"Copié sur le Bureau de l'invité : {names}");
                AppendDiagnostic($"Files: {result.Names.Count} item(s) copied to {result.GuestFolder} in the guest.");
            }
            catch (OperationCanceledException) when (transfer.Cancel.IsCancellationRequested)
            {
                SetTransferOutcome(target, "Copie annulée : rien n'a été laissé dans l'invité.");
            }
            finally
            {
                _transfers.Remove(target);
                transfer.Cancel.Dispose();
                RaiseFileTransferProperties();
            }
        }

        /// <summary>
        /// Copies files and folders of the selection's guest into a folder of this PC (Sharing.GuestFilePull),
        /// shown in the console bar as a drop is. Returns where they landed, or null when cancelled; failures are
        /// thrown for the view to show.
        /// </summary>
        public async Task<DropResult?> PullFilesAsync(IReadOnlyList<string> guestPaths, string destination)
        {
            if (SelectedVm is not { } target || SelectedSession is not { IsRunning: true } session)
                throw new InvalidOperationException("La machine doit être en marche.");
            if (_transfers.ContainsKey(target))
                throw new InvalidOperationException("Une copie avec cette machine est déjà en cours : attendez qu'elle se termine.");

            var transfer = new FileTransfer { Label = "Copie depuis l'invité" };
            _transfers[target] = transfer;
            _transferOutcomes.Remove(target);
            RaiseFileTransferProperties();
            try
            {
                var progress = new Progress<DropProgress>(p =>
                {
                    transfer.Sent = p.BytesSent;
                    transfer.Total = p.TotalBytes;
                    if (ReferenceEquals(SelectedVm, target))
                    {
                        RaisePropertyChanged(nameof(SendFilesPercent));
                        RaisePropertyChanged(nameof(SendFilesStatus));
                    }
                });
                var result = await GuestFilePull.PullAsync(session, guestPaths, destination, GuestTools.IsWindows(target), progress,
                    transfer.Cancel.Token);
                var names = result.Names.Count <= 3
                    ? string.Join(", ", result.Names)
                    : $"{string.Join(", ", result.Names.Take(2))} et {result.Names.Count - 2} autres";
                SetTransferOutcome(target, $"Copié dans {destination} : {names}");
                AppendDiagnostic($"Files: {result.Names.Count} item(s) copied from the guest to {destination}.");
                return result;
            }
            catch (OperationCanceledException) when (transfer.Cancel.IsCancellationRequested)
            {
                SetTransferOutcome(target, "Copie annulée : rien n'a été laissé sur ce PC.");
                return null;
            }
            finally
            {
                _transfers.Remove(target);
                transfer.Cancel.Dispose();
                RaiseFileTransferProperties();
            }
        }

        /// <summary>Where browsing the selection's guest starts, and a folder's entries: through the agent.</summary>
        public async Task<string> GuestHomeAsync()
        {
            await using var agent = await GuestAgentForBrowsingAsync();
            return await GuestFilePull.HomeAsync(agent, IsSelectedWindowsGuest);
        }

        public async Task<IReadOnlyList<GuestFileEntry>> ListGuestFolderAsync(string path)
        {
            await using var agent = await GuestAgentForBrowsingAsync();
            return await GuestFilePull.ListAsync(agent, IsSelectedWindowsGuest, path);
        }

        private async Task<GuestAgentClient> GuestAgentForBrowsingAsync()
        {
            if (SelectedSession is not { IsRunning: true } session)
                throw new InvalidOperationException("La machine doit être en marche.");
            return await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromSeconds(15))
                   ?? throw new GuestAgentException("L'agent invité ne répond pas : installez d'abord les outils invités.");
        }

        private void CancelSendFiles()
        {
            if (SelectedVm is { } vm && _transfers.TryGetValue(vm, out var transfer))
                transfer.Cancel.Cancel();
        }

        /// <summary>Shown in the console bar for a while, then the bar's hint comes back.</summary>
        private void SetTransferOutcome(VmConfig target, string text)
        {
            _transferOutcomes[target] = text;
            RaiseFileTransferProperties();
            _ = ClearTransferOutcomeAsync(target, text);
        }

        private async Task ClearTransferOutcomeAsync(VmConfig target, string text)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (_transferOutcomes.TryGetValue(target, out var current) && current == text)
            {
                _transferOutcomes.Remove(target);
                RaiseFileTransferProperties();
            }
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
