using System.Collections.ObjectModel;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Application-level entry point to the core: owns QEMU discovery, the VM library and the
    /// live sessions. The UI talks to this and never to QEMU directly.
    /// </summary>
    public sealed class VmManager : ObservableBase, IAsyncDisposable
    {
        private readonly Dictionary<Guid, VmSession> _sessions = new();

        public VmManager(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; }

        public ObservableCollection<VmConfig> VirtualMachines { get; } = new();

        /// <summary>Non-fatal problems from the last <see cref="InitializeAsync"/>, suitable for an info bar.</summary>
        public ObservableCollection<string> Warnings { get; } = new();

        private QemuDetectionResult? _detection;
        public QemuDetectionResult? Detection { get => _detection; private set => SetProperty(ref _detection, value); }

        /// <summary>Null until QEMU is found. Every QEMU-dependent feature must check this first.</summary>
        public QemuInstallation? Installation => Detection?.Installation;

        public bool IsQemuAvailable => Installation is not null;

        public QemuImgService? DiskService { get; private set; }

        public VmLibrary? Library { get; private set; }

        /// <summary>
        /// Detects QEMU and loads the library. Deliberately does not throw when QEMU is missing:
        /// the shell still has to run so it can explain the problem and let the user point at an install.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Warnings.Clear();

            // No ConfigureAwait(false) anywhere in this method: everything after the await touches
            // data-bound collections and raises PropertyChanged, which must happen on the thread
            // that owns them. Resuming on the thread pool here silently loses UI updates.
            Detection = await QemuLocator.DetectAsync(Settings.QemuDirectory, cancellationToken);

            RaisePropertyChanged(nameof(Installation));
            RaisePropertyChanged(nameof(IsQemuAvailable));

            if (Installation is not null)
                DiskService = new QemuImgService(Installation);

            foreach (var rejected in Detection.Rejected)
                Warnings.Add($"{rejected.Directory}: {rejected.Reason}");

            Library = new VmLibrary(Settings.EffectiveLibraryRoot, DiskService);
            ReloadLibrary();
        }

        /// <summary>
        /// Moves the library to another folder and rescans. Existing VMs are left where they are —
        /// this changes where new machines are created and where the shell looks, it does not migrate
        /// anything, because moving multi-gigabyte disks behind the user's back would be worse.
        /// </summary>
        public void SetLibraryRoot(string path)
        {
            Settings.LibraryRoot = path;
            Settings.Save();

            Library = new VmLibrary(Settings.EffectiveLibraryRoot, DiskService);
            ReloadLibrary();
        }

        public void ReloadLibrary()
        {
            if (Library is null)
                return;

            var machines = Library.Scan(out var warnings);
            foreach (var warning in warnings)
                Warnings.Add(warning);

            // Keep configs that already have a live session, so restarting the scan never orphans
            // a running VM from its entry in the list.
            VirtualMachines.Clear();
            foreach (var machine in machines)
            {
                if (_sessions.TryGetValue(machine.Id, out var session) && session.IsRunning)
                    VirtualMachines.Add(session.Config);
                else
                    VirtualMachines.Add(machine);
            }
        }

        public async Task<VmConfig> CreateVirtualMachineAsync(
            CreateVmRequest request,
            CancellationToken cancellationToken = default)
        {
            var library = Library ?? throw new InvalidOperationException("The VM library is not initialised.");
            // Resumes on the caller's context on purpose — VirtualMachines is data-bound.
            var config = await library.CreateAsync(request, cancellationToken);
            VirtualMachines.Add(config);
            return config;
        }

        /// <summary>
        /// Copies a VM under a new name and adds it to the library. Refused while the source has a QEMU
        /// process, paused included: qemu-img cannot read an image QEMU holds open, and forcing it would
        /// copy a disk caught mid-write.
        /// </summary>
        public async Task<VmConfig> CloneVirtualMachineAsync(
            VmConfig source,
            string name,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var library = Library ?? throw new InvalidOperationException("The VM library is not initialised.");

            if (FindSession(source) is { } session && session.State != VmState.Stopped)
                throw new InvalidOperationException("Arrêtez la machine virtuelle avant de la cloner.");

            // Resumes on the caller's context on purpose — VirtualMachines is data-bound.
            var clone = await library.CloneAsync(source, name, progress, cancellationToken);
            VirtualMachines.Add(clone);
            return clone;
        }

        /// <summary>
        /// Makes a linked clone: a VM whose disks hold only what it writes, reading everything else from the
        /// source's current images, which the source stops writing to. Refused while the source has a QEMU
        /// process, for the same reason as a full copy, and because the source's disks get a new layer.
        /// </summary>
        /// <remarks>
        /// Recorded in a fixed order: source first, clone last. Interrupted in between, the source merely has
        /// an extra snapshot level and the clone does not exist; the other order could leave a clone reading
        /// an image the source still writes to. Must be called on the UI thread: the source's disks are
        /// data-bound, and pushing an overlay raises PropertyChanged.
        /// </remarks>
        public async Task<VmConfig> CreateLinkedCloneAsync(
            VmConfig source,
            string name,
            CancellationToken cancellationToken = default)
        {
            var library = Library ?? throw new InvalidOperationException("The VM library is not initialised.");

            if (FindSession(source) is { } session && session.State != VmState.Stopped)
                throw new InvalidOperationException("Arrêtez la machine virtuelle avant de la cloner.");

            // Resumes on the caller's context on purpose — the source's disks are data-bound.
            var plan = await library.PrepareLinkedCloneAsync(source, name, cancellationToken);

            var pushed = new List<DiskConfig>();
            try
            {
                foreach (var (disk, overlay) in plan.SourceOverlays)
                {
                    disk.PushOverlay(overlay);
                    pushed.Add(disk);
                }

                library.Save(source);
                library.Save(plan.CloneConfig);
            }
            catch
            {
                // Popped in reverse so each disk's chain is exactly what it was.
                for (var index = pushed.Count - 1; index >= 0; index--)
                    pushed[index].PopOverlay();
                if (pushed.Count > 0)
                    TrySave(library, source);

                VmLibrary.AbandonLinkedClone(plan);
                throw;
            }

            VirtualMachines.Add(plan.CloneConfig);
            return plan.CloneConfig;
        }

        private static void TrySave(VmLibrary library, VmConfig config)
        {
            try
            {
                library.Save(config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The in-memory descriptor is right again; the next save of this machine writes it.
            }
        }

        private IReadOnlyList<LinkedCloneDependency> LinkedClones => LinkedCloneGuard.Dependencies(VirtualMachines);

        /// <summary>Why undoing or merging the disk's top overlay is refused, or null when it is allowed.</summary>
        public string? WhyOverlayIsLocked(VmConfig machine, DiskConfig disk) =>
            LinkedCloneGuard.WhyOverlayCannotBeRemoved(machine, disk, 0, LinkedClones);

        /// <summary>Why restoring or deleting a live snapshot is refused, or null when it is allowed.</summary>
        public string? WhyLiveSnapshotIsLocked(VmConfig machine, string name) =>
            LinkedCloneGuard.WhyLiveSnapshotIsLocked(machine, name, LinkedClones);

        /// <summary>Why deleting the machine's files is refused, or null when it is allowed.</summary>
        public string? WhyFilesCannotBeDeleted(VmConfig machine) =>
            LinkedCloneGuard.WhyFilesCannotBeDeleted(machine, LinkedClones);

        /// <summary>Why attaching this image as a disk is refused, or null when it is allowed.</summary>
        public string? WhyImageCannotBeAttached(string path) =>
            LinkedCloneGuard.WhyImageCannotBeAttached(path, LinkedClones);

        /// <summary>
        /// "Debian (copie)", then "Debian (copie 2)" and so on: the first name no machine in the library
        /// already uses.
        /// </summary>
        public string SuggestCloneName(VmConfig source)
        {
            var taken = new HashSet<string>(VirtualMachines.Select(m => m.Name.Trim()), StringComparer.CurrentCultureIgnoreCase);
            var baseName = source.Name.Trim();

            var candidate = $"{baseName} (copie)";
            for (var index = 2; taken.Contains(candidate); index++)
                candidate = $"{baseName} (copie {index.ToString(System.Globalization.CultureInfo.CurrentCulture)})";

            return candidate;
        }

        public void Save(VmConfig config) => Library?.Save(config);

        /// <summary>Returns the session for a VM, creating a stopped one on first request.</summary>
        public VmSession GetSession(VmConfig config)
        {
            if (_sessions.TryGetValue(config.Id, out var existing))
                return existing;

            var installation = Installation
                ?? throw new QemuNotAvailableException("QEMU was not found, so no VM can be started.");

            var session = new VmSession(config, installation);
            _sessions[config.Id] = session;
            SessionCreated?.Invoke(this, session);
            return session;
        }

        /// <summary>Raised when a VM gets its session, so services spanning all VMs can follow it.</summary>
        public event EventHandler<VmSession>? SessionCreated;

        public VmSession? FindSession(VmConfig config) =>
            _sessions.TryGetValue(config.Id, out var session) ? session : null;

        public IEnumerable<VmSession> RunningSessions => _sessions.Values.Where(s => s.IsRunning);

        public async Task DeleteVirtualMachineAsync(
            VmConfig config,
            bool deleteFiles,
            CancellationToken cancellationToken = default)
        {
            // Checked before anything is stopped or forgotten, so a refusal leaves the machine as it was.
            if (deleteFiles && WhyFilesCannotBeDeleted(config) is { } refusal)
                throw new InvalidOperationException(refusal);

            if (_sessions.TryGetValue(config.Id, out var session))
            {
                // Resumes on the caller's context on purpose — VirtualMachines is data-bound.
                if (session.IsRunning)
                    await session.PowerOffAsync(cancellationToken);

                _sessions.Remove(config.Id);
                await session.DisposeAsync();
            }

            Library?.Delete(config, deleteFiles);
            VirtualMachines.Remove(config);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in _sessions.Values.ToArray())
                await session.DisposeAsync().ConfigureAwait(false);

            _sessions.Clear();
        }
    }
}
