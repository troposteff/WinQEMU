using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.ViewModels
{
    /// <summary>One entry of the boot list, naming a device the firmware can be told to try.</summary>
    public sealed class BootEntryItem
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Detail { get; init; }
    }

    /// <summary>
    /// Editing surface for the parts of a VM that only take effect at the next start: boot order,
    /// disks and network adapters.
    /// </summary>
    /// <remarks>
    /// Kept out of <see cref="MainViewModel"/> because it owns something that view-model does not: a
    /// subscription graph. Neither a collection change on <c>Disks</c> nor a property change on a
    /// <see cref="DiskConfig"/> reaches the config's own PropertyChanged, so the shell's save debounce
    /// would never see these edits and they would be lost on the next reload.
    /// </remarks>
    public sealed class VmHardwareViewModel : ObservableObject
    {
        private readonly VmManager _manager;
        private readonly Action _requestSave;
        private readonly Action<Exception> _reportError;

        private VmConfig? _config;

        public VmHardwareViewModel(VmManager manager, Action requestSave, Action<Exception> reportError)
        {
            _manager = manager;
            _requestSave = requestSave;
            _reportError = reportError;

            MoveBootUpCommand = new RelayCommand(_ => MoveBootEntry(-1), _ => CanMoveBootEntry(-1));
            MoveBootDownCommand = new RelayCommand(_ => MoveBootEntry(+1), _ => CanMoveBootEntry(+1));
            RemoveDiskCommand = new RelayCommand(p => RemoveDisk(p as DiskConfig), _ => CanEdit);
            AddAdapterCommand = new RelayCommand(_ => AddAdapter(), _ => CanEdit);
            RemoveAdapterCommand = new RelayCommand(p => RemoveAdapter(p as NetworkAdapterConfig), _ => CanEdit);
            GenerateMacCommand = new RelayCommand(p => GenerateMac(p as NetworkAdapterConfig), _ => CanEdit);
            AddPortForwardCommand = new RelayCommand(p => AddPortForward(p as NetworkAdapterConfig), _ => CanEdit);
            RemovePortForwardCommand = new RelayCommand(p => RemovePortForward(p as PortForward), _ => CanEdit);
        }

        // ---- Attachment ----

        public VmConfig? Config
        {
            get => _config;
            private set => SetProperty(ref _config, value);
        }

        /// <summary>Points the editor at another machine, moving all subscriptions with it.</summary>
        public void Attach(VmConfig? config)
        {
            if (ReferenceEquals(_config, config))
                return;

            Untrack(_config);
            Config = config;
            Track(_config);

            RebuildBootEntries();
            RefreshPlatformDiagnostics();
            _ = EnsureCapabilitiesAsync();
            RaisePropertyChanged(nameof(Disks));
            RaisePropertyChanged(nameof(Adapters));
            RaisePropertyChanged(nameof(HasDisks));
            RaisePropertyChanged(nameof(HasAdapters));
        }

        private bool _canEdit;
        /// <summary>
        /// False while the machine is alive. None of these settings can be applied to a running QEMU
        /// process, so they are locked rather than silently deferred.
        /// </summary>
        public bool CanEdit
        {
            get => _canEdit;
            set
            {
                if (!SetProperty(ref _canEdit, value))
                    return;

                RaisePropertyChanged(nameof(IsLocked));
                RaiseAllCanExecuteChanged();
            }
        }

        public bool IsLocked => !CanEdit;

        // ---- Boot order ----

        public ObservableCollection<BootEntryItem> BootEntries { get; } = new();

        private BootEntryItem? _selectedBootEntry;
        public BootEntryItem? SelectedBootEntry
        {
            get => _selectedBootEntry;
            set
            {
                if (!SetProperty(ref _selectedBootEntry, value))
                    return;

                MoveBootUpCommand.RaiseCanExecuteChanged();
                MoveBootDownCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand MoveBootUpCommand { get; }
        public RelayCommand MoveBootDownCommand { get; }

        private void RebuildBootEntries()
        {
            var previousId = SelectedBootEntry?.Id;
            BootEntries.Clear();

            if (_config is null)
            {
                SelectedBootEntry = null;
                return;
            }

            // The builder decides which devices are bootable and in what order they would be tried by
            // default; asking it keeps the list honest instead of re-deriving the same rules here.
            var available = QemuArgumentBuilder.EnumerateBootableIds(_config);
            var ranked = _config.BootOrder
                .Where(id => available.Contains(id, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ranked.AddRange(available.Where(id => !ranked.Contains(id, StringComparer.Ordinal)));

            foreach (var id in ranked)
                BootEntries.Add(DescribeBootEntry(_config, id));

            SelectedBootEntry = BootEntries.FirstOrDefault(e => e.Id == previousId) ?? BootEntries.FirstOrDefault();
            RaisePropertyChanged(nameof(HasBootEntries));
        }

        public bool HasBootEntries => BootEntries.Count > 0;

        private static BootEntryItem DescribeBootEntry(VmConfig config, string id)
        {
            if (id == QemuArgumentBuilder.PrimaryCdId)
                return new BootEntryItem { Id = id, Label = "Lecteur optique", Detail = FileLabel(config.IsoPath) };

            if (id == QemuArgumentBuilder.SecondaryCdId)
                return new BootEntryItem { Id = id, Label = "Second lecteur optique", Detail = FileLabel(config.SecondaryIsoPath) };

            var disk = config.Disks.FirstOrDefault(d => d.Id == id);
            return new BootEntryItem
            {
                Id = id,
                Label = $"Disque {id}",
                Detail = disk is null ? string.Empty : FileLabel(disk.FileName)
            };
        }

        private static string FileLabel(string path) =>
            string.IsNullOrWhiteSpace(path) ? "(vide)" : Path.GetFileName(path);

        private bool CanMoveBootEntry(int delta)
        {
            if (!CanEdit || SelectedBootEntry is null)
                return false;

            var index = BootEntries.IndexOf(SelectedBootEntry);
            var target = index + delta;
            return index >= 0 && target >= 0 && target < BootEntries.Count;
        }

        private void MoveBootEntry(int delta)
        {
            if (!CanMoveBootEntry(delta) || _config is null || SelectedBootEntry is null)
                return;

            var index = BootEntries.IndexOf(SelectedBootEntry);
            BootEntries.Move(index, index + delta);

            _config.BootOrder.Clear();
            foreach (var entry in BootEntries)
                _config.BootOrder.Add(entry.Id);

            // BootFromIso is what the wizard and older descriptors express their intent with. Keeping
            // it consistent means the two never contradict each other about what boots first.
            _config.BootFromIso = BootEntries.FirstOrDefault()?.Id == QemuArgumentBuilder.PrimaryCdId;

            MoveBootUpCommand.RaiseCanExecuteChanged();
            MoveBootDownCommand.RaiseCanExecuteChanged();
            _requestSave();
        }

        // ---- Disks ----

        public ObservableCollection<DiskConfig>? Disks => _config?.Disks;

        public bool HasDisks => _config?.Disks.Count > 0;

        public IReadOnlyList<DiskBus> BusOptions { get; } = Enum.GetValues<DiskBus>();
        public IReadOnlyList<DiskCacheMode> CacheOptions { get; } = Enum.GetValues<DiskCacheMode>();
        public IReadOnlyList<DiskFormat> FormatOptions { get; } = Enum.GetValues<DiskFormat>();

        public RelayCommand RemoveDiskCommand { get; }

        /// <summary>Attaches an image that already exists, reading its real geometry from qemu-img.</summary>
        public async Task AttachExistingDiskAsync(string path)
        {
            if (_config is null)
                return;

            try
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full))
                    throw new FileNotFoundException($"Image introuvable : {full}", full);

                if (_manager.WhyImageCannotBeAttached(full) is { } reason)
                    throw new InvalidOperationException(reason);

                var format = DiskFormat.Qcow2;
                long capacity = 0;

                if (_manager.DiskService is { } service)
                {
                    var info = await service.GetInfoAsync(full).ConfigureAwait(true);
                    format = ParseFormat(info.Format);
                    capacity = info.VirtualSizeBytes;
                }

                _config.Disks.Add(new DiskConfig
                {
                    Id = NextDiskId(),
                    FileName = full,
                    Format = format,
                    Bus = DefaultBus(),
                    CapacityBytes = capacity
                });

                _requestSave();
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        /// <summary>Creates a new image inside the VM folder and attaches it.</summary>
        public async Task CreateDiskAsync(long capacityBytes, DiskFormat format, DiskBus bus)
        {
            if (_config is null)
                return;

            try
            {
                if (_manager.DiskService is null)
                    throw new InvalidOperationException("qemu-img n'est pas disponible : QEMU n'a pas été détecté.");

                var id = NextDiskId();
                var fileName = $"{id}.{DiskExtension(format)}";
                var fullPath = Path.Combine(_config.FolderPath, fileName);

                if (File.Exists(fullPath))
                    throw new IOException($"Le fichier {fileName} existe déjà dans le dossier de la machine.");

                await _manager.DiskService.CreateAsync(fullPath, format, capacityBytes, backingFile: null)
                    .ConfigureAwait(true);

                _config.Disks.Add(new DiskConfig
                {
                    Id = id,
                    // Relative, so the VM folder stays movable as a unit.
                    FileName = fileName,
                    Format = format,
                    Bus = bus,
                    CapacityBytes = capacityBytes
                });

                _requestSave();
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        /// <summary>Enlarges an image and records the new capacity in the descriptor.</summary>
        public async Task GrowDiskAsync(DiskConfig disk, long newCapacityBytes)
        {
            if (_config is null || !CanEdit)
                return;

            try
            {
                if (_manager.DiskService is null)
                    throw new InvalidOperationException("qemu-img n'est pas disponible : QEMU n'a pas été détecté.");

                await _manager.DiskService.GrowAsync(ResolveDiskPath(disk), newCapacityBytes).ConfigureAwait(true);

                // Read the geometry back rather than assuming the requested value was applied verbatim.
                var info = await _manager.DiskService.GetInfoAsync(ResolveDiskPath(disk)).ConfigureAwait(true);
                disk.CapacityBytes = info.VirtualSizeBytes;

                _requestSave();
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        /// <summary>
        /// Converts an image to another format and attaches the copy.
        /// </summary>
        /// <remarks>
        /// The original is kept. qemu-img convert copies the current contents and not the snapshots, so
        /// the only place the history still exists afterwards is that file — deleting it would silently
        /// throw the history away.
        /// </remarks>
        public async Task ConvertDiskAsync(DiskConfig disk, DiskFormat format, bool compress)
        {
            if (_config is null || !CanEdit)
                return;

            try
            {
                if (_manager.DiskService is null)
                    throw new InvalidOperationException("qemu-img n'est pas disponible : QEMU n'a pas été détecté.");

                var source = ResolveDiskPath(disk);
                var targetName = $"{disk.Id}.{DiskExtension(format)}";
                var target = Path.Combine(_config.FolderPath, targetName);

                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
                    throw new IOException($"Le fichier {targetName} existe déjà dans le dossier de la machine.");

                await _manager.DiskService.ConvertAsync(source, target, format, compress).ConfigureAwait(true);

                // Relative, so the VM folder stays movable as a unit.
                disk.FileName = targetName;
                disk.Format = format;

                // convert read through the base, so a linked clone's disk now holds everything itself —
                // unless the disk is an overlay: its recorded chain still ends on the image that reads the
                // base, and undoing the overlay would go back to it.
                if (!disk.IsOverlay && disk.IsLinkedClone)
                {
                    // What the clone read until now, before the link that says so is gone: the original
                    // may have kept some of it only for this clone.
                    var formerlyRead = _manager.ImagesReadThrough(_config);
                    disk.LinkedBaseFileName = string.Empty;
                    _manager.ReleaseRetainedImages(formerlyRead);
                }

                _requestSave();
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        /// <summary>
        /// Discards a live-snapshot overlay: the machine goes back to the image underneath and everything
        /// written since the snapshot is lost. When a linked clone reads that image, the machine restarts
        /// from it on a fresh overlay instead, and the clone's images are kept.
        /// </summary>
        public async Task DiscardOverlayAsync(DiskConfig disk)
        {
            if (_config is null || !CanEdit || !disk.IsOverlay)
                return;

            try
            {
                if (_manager.DiskService is null)
                    throw new InvalidOperationException("qemu-img n'est pas disponible : QEMU n'a pas été détecté.");

                await new LiveSnapshotOperations(_manager.DiskService)
                    .RevertTopAsync(_config, disk, _manager.ProtectedImages).ConfigureAwait(true);
                _requestSave();
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        /// <summary>
        /// Folds a live-snapshot overlay into the image underneath: everything written since is kept and
        /// the snapshot is gone.
        /// </summary>
        public async Task CommitOverlayAsync(DiskConfig disk)
        {
            if (_config is null || !CanEdit || !disk.IsOverlay)
                return;

            try
            {
                if (_manager.DiskService is null)
                    throw new InvalidOperationException("qemu-img n'est pas disponible : QEMU n'a pas été détecté.");

                if (_manager.WhyOverlayCannotBeMerged(_config, disk) is { } reason)
                    throw new InvalidOperationException(reason);

                var overlay = ResolveDiskPath(disk);
                // qemu-img commit writes into the overlay's own base, which is the next level down even
                // when overlays are stacked.
                await _manager.DiskService.CommitAsync(overlay).ConfigureAwait(true);

                disk.PopOverlay();
                _requestSave();

                if (File.Exists(overlay))
                    File.Delete(overlay);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        /// <summary>Detaches a disk. The image file is left on disk; deleting it is a separate step.</summary>
        private void RemoveDisk(DiskConfig? disk)
        {
            if (disk is null || _config is null || !CanEdit)
                return;

            _config.Disks.Remove(disk);

            // Drop the stale id rather than leaving it to be filtered out on every launch.
            while (_config.BootOrder.Remove(disk.Id)) { }

            _requestSave();
        }

        /// <summary>Absolute path of a disk image, for the caller that needs to delete or inspect it.</summary>
        public string ResolveDiskPath(DiskConfig disk) => _config?.ResolvePath(disk.FileName) ?? disk.FileName;

        private string NextDiskId()
        {
            for (var index = 0; ; index++)
            {
                var candidate = $"disk{index.ToString(CultureInfo.InvariantCulture)}";
                if (_config!.Disks.All(d => d.Id != candidate))
                    return candidate;
            }
        }

        // ---- Platform ----

        /// <summary>
        /// Machine types and CPU models this QEMU offers, asked of the binary itself.
        /// </summary>
        /// <remarks>
        /// Both dropdowns stay editable. A machine type can carry sub-options and a CPU model is a name
        /// plus feature flags -- the shipped default is
        /// <c>qemu64,+sse4.1,+sse4.2,+popcnt,+aes,+ssse3,+xsave</c>, which is no entry in any list. A
        /// closed list would have thrown those flags away.
        /// </remarks>
        public ObservableCollection<string> MachineTypeOptions { get; } = new();

        public ObservableCollection<string> CpuModelOptions { get; } = new();

        // Probed once per architecture: each probe runs QEMU twice, and doing that on every selection
        // change would make arrowing through the inventory sluggish.
        private readonly Dictionary<GuestArchitecture, QemuCapabilities> _capabilities = new();

        private async Task EnsureCapabilitiesAsync()
        {
            var config = _config;
            var installation = _manager.Installation;
            if (config is null || installation is null || !installation.SupportsArchitecture(config.Architecture))
                return;

            var architecture = config.Architecture;
            try
            {
                if (!_capabilities.TryGetValue(architecture, out var capabilities))
                {
                    capabilities = await QemuCapabilities.ProbeAsync(installation, architecture).ConfigureAwait(true);
                    _capabilities[architecture] = capabilities;
                }

                // The selection may have moved on while QEMU was answering.
                if (!ReferenceEquals(_config, config) || _config.Architecture != architecture)
                    return;

                Replace(MachineTypeOptions, capabilities.MachineTypes);
                Replace(CpuModelOptions, capabilities.CpuModels);
            }
            catch (Exception ex)
            {
                // A failed probe leaves the dropdowns empty; they are editable, so the field still works.
                _reportError(ex);
            }
        }

        private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
        {
            target.Clear();
            foreach (var value in values)
                target.Add(value);
        }

        public IReadOnlyList<FirmwareType> FirmwareOptions { get; } = Enum.GetValues<FirmwareType>();
        public IReadOnlyList<AcceleratorType> AcceleratorOptions { get; } = Enum.GetValues<AcceleratorType>();
        public IReadOnlyList<GraphicsAdapter> GraphicsOptions { get; } = Enum.GetValues<GraphicsAdapter>();
        public IReadOnlyList<GuestArchitecture> ArchitectureOptions { get; } = Enum.GetValues<GuestArchitecture>();

        private string _platformProblems = string.Empty;
        /// <summary>
        /// What would stop this machine from starting, as the argument builder sees it.
        /// </summary>
        /// <remarks>
        /// Shown live rather than only on a failed start. Several of these combinations produce a machine
        /// that starts and then does nothing visible -- a CPU model of "max" under WHPX leaves the guest
        /// completely mute -- so the moment to say so is while the setting is being chosen.
        /// </remarks>
        public string PlatformProblems { get => _platformProblems; private set => SetProperty(ref _platformProblems, value); }

        public bool HasPlatformProblems => PlatformProblems.Length > 0;

        private string _commandPreview = string.Empty;
        /// <summary>
        /// The command line these settings produce. The only way to check what the raw-arguments escape
        /// hatch actually does, and the fastest way to see the effect of a platform change.
        /// </summary>
        public string CommandPreview { get => _commandPreview; private set => SetProperty(ref _commandPreview, value); }

        private void RefreshPlatformDiagnostics()
        {
            var installation = _manager.Installation;
            if (_config is null || installation is null)
            {
                PlatformProblems = string.Empty;
                CommandPreview = string.Empty;
                RaisePropertyChanged(nameof(HasPlatformProblems));
                return;
            }

            try
            {
                PlatformProblems = string.Join(Environment.NewLine, QemuArgumentBuilder.Validate(_config, installation));

                // Representative ports: the real ones are allocated at launch, and showing 0 would be a
                // command line that does not correspond to anything. Networks are only looked up, never
                // started — without them every card on a virtual network read as unplugged here, and the
                // panel claimed the machine would not start when it did.
                var networks = _manager.Settings.VirtualNetworks;
                var arguments = QemuArgumentBuilder.Build(_config, installation, 4444,
                    networkPorts: id => networks.Any(n => n.Id == id) ? 45000 : null);
                CommandPreview = QemuArgumentBuilder.ToDisplayString(
                    installation.GetSystemBinary(_config.Architecture),
                    arguments);
            }
            catch (Exception ex)
            {
                // Build throws when the firmware it needs is missing; that is a diagnostic, not a crash.
                PlatformProblems = ex.Message;
                CommandPreview = string.Empty;
            }

            RaisePropertyChanged(nameof(HasPlatformProblems));
        }

        /// <summary>Matches the bus of the existing disks so a second disk does not land on a different controller by accident.</summary>
        public DiskBus SuggestedBus => _config?.Disks.Count > 0 ? _config.Disks[0].Bus : DiskBus.VirtioBlk;

        private DiskBus DefaultBus() => SuggestedBus;

        private static DiskFormat ParseFormat(string? format) => format?.ToLowerInvariant() switch
        {
            "raw" => DiskFormat.Raw,
            "vhdx" => DiskFormat.Vhdx,
            "vmdk" => DiskFormat.Vmdk,
            "vdi" => DiskFormat.Vdi,
            _ => DiskFormat.Qcow2
        };

        private static string DiskExtension(DiskFormat format) => format switch
        {
            DiskFormat.Raw => "img",
            DiskFormat.Vhdx => "vhdx",
            DiskFormat.Vmdk => "vmdk",
            DiskFormat.Vdi => "vdi",
            _ => "qcow2"
        };

        // ---- Network ----

        public ObservableCollection<NetworkAdapterConfig>? Adapters => _config?.NetworkAdapters;

        public bool HasAdapters => _config?.NetworkAdapters.Count > 0;

        // The multicast segment is gone from the choices: its cards are moved to a virtual network on load.
        public IReadOnlyList<NetworkMode> ModeOptions { get; } = Enum.GetValues<NetworkMode>().Where(m => m != NetworkMode.Internal).ToList();
        public IReadOnlyList<NicModel> NicModelOptions { get; } = Enum.GetValues<NicModel>();
        public IReadOnlyList<PortForwardProtocol> ProtocolOptions { get; } = Enum.GetValues<PortForwardProtocol>();

        public RelayCommand AddAdapterCommand { get; }
        public RelayCommand RemoveAdapterCommand { get; }
        public RelayCommand GenerateMacCommand { get; }
        public RelayCommand AddPortForwardCommand { get; }
        public RelayCommand RemovePortForwardCommand { get; }

        private void AddAdapter()
        {
            if (_config is null || !CanEdit)
                return;

            _config.NetworkAdapters.Add(new NetworkAdapterConfig
            {
                Id = NextAdapterId(),
                Mode = NetworkMode.Nat,
                Model = NicModel.VirtioNet,
                // Generated up front: a fixed MAC is what keeps the guest's DHCP lease and its network
                // profile stable from one boot to the next.
                MacAddress = NetworkAdapterConfig.GenerateMacAddress()
            });

            _requestSave();
        }

        private void RemoveAdapter(NetworkAdapterConfig? adapter)
        {
            if (adapter is null || _config is null || !CanEdit)
                return;

            _config.NetworkAdapters.Remove(adapter);
            _requestSave();
        }

        private void GenerateMac(NetworkAdapterConfig? adapter)
        {
            if (adapter is null || !CanEdit)
                return;

            adapter.MacAddress = NetworkAdapterConfig.GenerateMacAddress();
            _requestSave();
        }

        private void AddPortForward(NetworkAdapterConfig? adapter)
        {
            if (adapter is null || !CanEdit)
                return;

            adapter.PortForwards.Add(new PortForward { HostPort = 0, GuestPort = 0 });
            _requestSave();
        }

        private void RemovePortForward(PortForward? forward)
        {
            if (forward is null || _config is null || !CanEdit)
                return;

            foreach (var adapter in _config.NetworkAdapters)
            {
                if (adapter.PortForwards.Remove(forward))
                    break;
            }

            _requestSave();
        }

        private string NextAdapterId()
        {
            for (var index = 0; ; index++)
            {
                var candidate = $"net{index.ToString(CultureInfo.InvariantCulture)}";
                if (_config!.NetworkAdapters.All(a => a.Id != candidate))
                    return candidate;
            }
        }

        // ---- Change tracking ----

        private void Track(VmConfig? config)
        {
            if (config is null)
                return;

            config.PropertyChanged += OnConfigPropertyChanged;

            config.Disks.CollectionChanged += OnDisksChanged;
            foreach (var disk in config.Disks)
                disk.PropertyChanged += OnChildPropertyChanged;

            config.NetworkAdapters.CollectionChanged += OnAdaptersChanged;
            foreach (var adapter in config.NetworkAdapters)
                TrackAdapter(adapter);
        }

        private void Untrack(VmConfig? config)
        {
            if (config is null)
                return;

            config.PropertyChanged -= OnConfigPropertyChanged;

            config.Disks.CollectionChanged -= OnDisksChanged;
            foreach (var disk in config.Disks)
                disk.PropertyChanged -= OnChildPropertyChanged;

            config.NetworkAdapters.CollectionChanged -= OnAdaptersChanged;
            foreach (var adapter in config.NetworkAdapters)
                UntrackAdapter(adapter);
        }

        private void TrackAdapter(NetworkAdapterConfig adapter)
        {
            adapter.PropertyChanged += OnChildPropertyChanged;
            adapter.PortForwards.CollectionChanged += OnPortForwardsChanged;
            foreach (var forward in adapter.PortForwards)
                forward.PropertyChanged += OnChildPropertyChanged;
        }

        private void UntrackAdapter(NetworkAdapterConfig adapter)
        {
            adapter.PropertyChanged -= OnChildPropertyChanged;
            adapter.PortForwards.CollectionChanged -= OnPortForwardsChanged;
            foreach (var forward in adapter.PortForwards)
                forward.PropertyChanged -= OnChildPropertyChanged;
        }

        private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Changing either ISO adds or removes a bootable device, so the boot list has to follow.
            if (e.PropertyName is nameof(VmConfig.IsoPath) or nameof(VmConfig.SecondaryIsoPath))
                RebuildBootEntries();

            // A different emulator offers different machines and CPUs.
            if (e.PropertyName == nameof(VmConfig.Architecture))
                _ = EnsureCapabilitiesAsync();

            // Any platform field can turn the configuration into one QEMU would refuse, so both the
            // verdict and the preview are recomputed on every edit.
            RefreshPlatformDiagnostics();
        }

        private void OnDisksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Resubscribe<DiskConfig>(e, disk => disk.PropertyChanged += OnChildPropertyChanged,
                disk => disk.PropertyChanged -= OnChildPropertyChanged);

            RebuildBootEntries();
            RaisePropertyChanged(nameof(HasDisks));
        }

        private void OnAdaptersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Resubscribe<NetworkAdapterConfig>(e, TrackAdapter, UntrackAdapter);
            RaisePropertyChanged(nameof(HasAdapters));
        }

        private void OnPortForwardsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            Resubscribe<PortForward>(e, forward => forward.PropertyChanged += OnChildPropertyChanged,
                forward => forward.PropertyChanged -= OnChildPropertyChanged);

        private static void Resubscribe<T>(NotifyCollectionChangedEventArgs e, Action<T> subscribe, Action<T> unsubscribe)
        {
            foreach (var item in e.OldItems?.OfType<T>() ?? Enumerable.Empty<T>())
                unsubscribe(item);
            foreach (var item in e.NewItems?.OfType<T>() ?? Enumerable.Empty<T>())
                subscribe(item);
        }

        private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is DiskConfig && e.PropertyName is nameof(DiskConfig.Id) or nameof(DiskConfig.FileName))
                RebuildBootEntries();

            // A card's mode, network or forwards decide whether the machine can start: a card switched to
            // a virtual network kept saying it was unplugged after one had been picked.
            if (sender is NetworkAdapterConfig or PortForward)
                RefreshPlatformDiagnostics();

            _requestSave();
        }

        private void RaiseAllCanExecuteChanged()
        {
            MoveBootUpCommand.RaiseCanExecuteChanged();
            MoveBootDownCommand.RaiseCanExecuteChanged();
            RemoveDiskCommand.RaiseCanExecuteChanged();
            AddAdapterCommand.RaiseCanExecuteChanged();
            RemoveAdapterCommand.RaiseCanExecuteChanged();
            GenerateMacCommand.RaiseCanExecuteChanged();
            AddPortForwardCommand.RaiseCanExecuteChanged();
            RemovePortForwardCommand.RaiseCanExecuteChanged();
        }
    }
}
