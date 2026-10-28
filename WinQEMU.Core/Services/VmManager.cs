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

        /// <summary>The switches of the virtual networks, started as machines on them start.</summary>
        public Networking.VirtualNetworkHost Networks { get; } = new();

        private int? NetworkPort(string networkId) =>
            Settings.VirtualNetworks.FirstOrDefault(n => n.Id == networkId) is { } network ? Networks.PortOf(network, Settings.VirtualNetworks) : null;

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

            MoveMulticastSegmentsToNetworks();
        }

        /// <summary>
        /// Plugs cards of the old multicast "internal" mode into a private network per segment number
        /// ("Segment 2"), so machines that shared a segment still share a network, now through the switch.
        /// </summary>
        private void MoveMulticastSegmentsToNetworks()
        {
            var changedSettings = false;
            foreach (var machine in VirtualMachines)
            {
                var changed = false;
                foreach (var adapter in machine.NetworkAdapters.Where(a => a.Mode == NetworkMode.Internal))
                {
                    var name = $"Segment {adapter.InternalSegment.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    var network = Settings.VirtualNetworks.FirstOrDefault(n => n.Name == name);
                    if (network is null)
                    {
                        network = new VirtualNetwork { Name = name, Subnet = VirtualNetwork.FreeSubnet(Settings.VirtualNetworks) };
                        Settings.VirtualNetworks.Add(network);
                        changedSettings = true;
                    }
                    adapter.Mode = NetworkMode.Network;
                    adapter.NetworkId = network.Id;
                    changed = true;
                }
                if (changed)
                    Save(machine);
            }
            if (changedSettings)
                Settings.Save();
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
        /// Exports a VM as an .ova. Refused while it has a QEMU process, paused included, for the same reason
        /// as a clone: qemu-img would read disks caught mid-write.
        /// </summary>
        public Task ExportVirtualMachineAsync(
            VmConfig source,
            string destination,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var images = DiskService ?? throw new QemuNotAvailableException("QEMU is required to export a virtual machine.");

            if (FindSession(source) is { } session && session.State != VmState.Stopped)
                throw new InvalidOperationException("Arrêtez la machine virtuelle avant de l'exporter.");

            return Export.OvaExporter.ExportAsync(source, destination, images, progress, cancellationToken);
        }

        /// <summary>Imports a machine of another hypervisor and adds it to the library.</summary>
        public async Task<VmConfig> ImportVirtualMachineAsync(
            Import.ImportPlan plan,
            string name,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var library = Library ?? throw new InvalidOperationException("The VM library is not initialised.");

            // Resumes on the caller's context on purpose — VirtualMachines is data-bound.
            var config = await library.ImportAsync(plan, name, progress, cancellationToken);
            VirtualMachines.Add(config);
            return config;
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

        private LinkedCloneProtection Protection => LinkedCloneProtection.Of(VirtualMachines);

        /// <summary>
        /// Whether a linked clone reads the image at an absolute path, as of now — for
        /// <see cref="LiveSnapshotOperations.RevertAsync"/>, which keeps such images.
        /// </summary>
        public Func<string, bool> ProtectedImages => Protection.IsProtected;

        /// <summary>Why merging the disk's top overlay into its base is refused, or null when it is allowed.</summary>
        public string? WhyOverlayCannotBeMerged(VmConfig machine, DiskConfig disk) =>
            LinkedCloneGuard.WhyOverlayCannotBeMerged(machine, disk, 0, Protection);

        /// <summary>Why deleting a live snapshot is refused, or null when it is allowed.</summary>
        public string? WhyLiveSnapshotCannotBeDeleted(VmConfig machine, string name) =>
            LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(machine, name, Protection);

        /// <summary>Why deleting the machine's files is refused, or null when it is allowed.</summary>
        public string? WhyFilesCannotBeDeleted(VmConfig machine) =>
            LinkedCloneGuard.WhyFilesCannotBeDeleted(machine, Protection);

        /// <summary>Why attaching this image as a disk is refused, or null when it is allowed.</summary>
        public string? WhyImageCannotBeAttached(string path) =>
            LinkedCloneGuard.WhyImageCannotBeAttached(path, Protection);

        /// <summary>
        /// The images a linked clone read through its disks, for <see cref="ReleaseRetainedImages"/> once it
        /// stops reading them. Empty for a machine that is not a linked clone.
        /// </summary>
        public IReadOnlyList<string> ImagesReadThrough(VmConfig clone)
        {
            var below = LinkedCloneProtection.BackingMap(VirtualMachines);
            return clone.Disks
                .Where(d => d.IsLinkedClone)
                .SelectMany(d => LinkedCloneProtection.LineFrom(clone.ResolvePath(d.LinkedBaseFileName), below))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Deletes the images among <paramref name="formerlyRead"/> that were kept only for linked clones
        /// and that no listed clone reads any more, and saves the machines that kept them.
        /// </summary>
        /// <remarks>
        /// Limited to what one clone used to read, rather than sweeping every kept image in the library: a
        /// clone only removed from the list may still exist on disk, and a library-wide sweep triggered by
        /// some unrelated deletion would destroy what it reads without the user having touched it.
        /// </remarks>
        public void ReleaseRetainedImages(IEnumerable<string> formerlyRead)
        {
            var library = Library;
            if (library is null)
                return;

            var candidates = new HashSet<string>(formerlyRead.Select(LinkedCloneProtection.Normalize), StringComparer.OrdinalIgnoreCase);
            var protection = Protection;

            foreach (var machine in VirtualMachines)
            {
                var changed = false;
                foreach (var disk in machine.Disks)
                {
                    foreach (var retained in disk.RetainedImages.ToList())
                    {
                        var path = LinkedCloneProtection.Normalize(machine.ResolvePath(retained.FileName));
                        if (!candidates.Contains(path) || protection.IsProtected(path))
                            continue;

                        try
                        {
                            if (File.Exists(path))
                                File.Delete(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Kept on record so a later release tries again, rather than losing track of it.
                            continue;
                        }

                        disk.RetainedImages.Remove(retained);
                        changed = true;
                    }
                }

                if (changed)
                    TrySave(library, machine);
            }
        }

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

            var session = new VmSession(config, installation, NetworkPort);
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

            // Read before the clone leaves the list: afterwards nothing says what it used to read.
            var formerlyRead = deleteFiles ? ImagesReadThrough(config) : Array.Empty<string>();

            Library?.Delete(config, deleteFiles);
            VirtualMachines.Remove(config);

            // Only for a real deletion: a clone merely removed from the list still exists on disk and may
            // be added back, so what it reads stays.
            if (formerlyRead.Count > 0)
                ReleaseRetainedImages(formerlyRead);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in _sessions.Values.ToArray())
                await session.DisposeAsync().ConfigureAwait(false);
            await Networks.DisposeAsync().ConfigureAwait(false);

            _sessions.Clear();
        }
    }
}
