using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Presentation wrapper over a <see cref="VmSnapshot"/>. Only the wording lives here; what a
    /// snapshot is, which disks it spans and whether it is partial are decided in the core.
    /// </summary>
    public sealed class SnapshotItem
    {
        public SnapshotItem(VmSnapshot snapshot) => Snapshot = snapshot;

        public VmSnapshot Snapshot { get; }

        public string Name => Snapshot.Name;

        public bool IsPartial => Snapshot.IsPartial;

        public string CreatedText =>
            Snapshot.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);

        public string DetailText
        {
            get
            {
                var parts = new List<string>
                {
                    Snapshot.IsLive
                        ? "à chaud · disques"
                        : Snapshot.HasMemoryState
                            ? $"mémoire {Snapshot.VmStateSizeBytes / 1024d / 1024d:0.#} Mio"
                            : "état disque"
                };

                // Only worth saying when the machine has more than one disk to be inconsistent about.
                if (Snapshot.TotalDisks > 1)
                    parts.Add($"{Snapshot.PresentOnDisks}/{Snapshot.TotalDisks} disques");

                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Snapshots of the selected machine.
    /// </summary>
    /// <remarks>
    /// These are qcow2 internal snapshots taken with qemu-img while the machine is off, so they capture
    /// disk state only. Saving guest memory needs QMP savevm, which requires migration support that
    /// WHPX does not provide — under hardware acceleration there is no way to snapshot a running guest,
    /// and pretending otherwise would produce snapshots that silently restore to a crashed machine.
    /// </remarks>
    public sealed class VmSnapshotsViewModel : ObservableObject
    {
        private readonly VmManager _manager;
        private readonly Action _requestSave;
        private readonly Action<Exception> _reportError;

        private VmConfig? _config;
        private bool _loaded;

        public VmSnapshotsViewModel(VmManager manager, Action requestSave, Action<Exception> reportError)
        {
            _manager = manager;
            _requestSave = requestSave;
            _reportError = reportError;
        }

        public ObservableCollection<SnapshotItem> Snapshots { get; } = new();

        private SnapshotItem? _selected;
        public SnapshotItem? Selected
        {
            get => _selected;
            set
            {
                if (!SetProperty(ref _selected, value))
                    return;

                RaisePropertyChanged(nameof(HasSelection));
                RaisePropertyChanged(nameof(CanActOnSelection));
                RaisePropertyChanged(nameof(CanResumeSelection));
            }
        }

        public bool HasSelection => Selected is not null;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!SetProperty(ref _isBusy, value))
                    return;

                RaisePropertyChanged(nameof(CanManage));
                RaisePropertyChanged(nameof(CanCreate));
                RaisePropertyChanged(nameof(CanActOnSelection));
                RaisePropertyChanged(nameof(CanResumeSelection));
                RaisePropertyChanged(nameof(CanTakeLiveSnapshot));
                RaisePropertyChanged(nameof(CanTakeMemorySnapshot));
                RaisePropertyChanged(nameof(CanEnterName));
            }
        }

        private bool _isMachineOff;
        /// <summary>
        /// Set by the shell from the machine's state. qemu-img refuses to touch an image a running QEMU
        /// has open, and forcing it would corrupt the disk.
        /// </summary>
        public bool IsMachineOff
        {
            get => _isMachineOff;
            set
            {
                if (!SetProperty(ref _isMachineOff, value))
                    return;

                RaisePropertyChanged(nameof(CanManage));
                RaisePropertyChanged(nameof(CanCreate));
                RaisePropertyChanged(nameof(CanActOnSelection));
                RaisePropertyChanged(nameof(IsLockedByRunningMachine));
            }
        }

        public bool CanManage => IsMachineOff && !IsBusy && _config is not null && SnapshotableDisks.Count > 0;

        /// <summary>
        /// Whether a name can be typed at all. Not the same as CanManage: the two live forms are taken
        /// while the machine runs, so tying the field to a stopped machine made it unusable exactly when
        /// they apply.
        /// </summary>
        public bool CanEnterName =>
            !IsBusy && _config is not null && (IsMachineOff || Session?.IsRunning == true);

        /// <summary>True while a live machine holds its images open, which is why nothing can be done.</summary>
        public bool IsLockedByRunningMachine => !IsMachineOff;

        /// <summary>Whether the selected snapshot can actually be acted on. Bound by Restaurer and
        /// Supprimer: with HasSelection alone they stayed clickable on a running machine and did nothing.</summary>
        public bool CanActOnSelection => CanManage && HasSelection;

        private string _newName = string.Empty;
        public string NewName
        {
            get => _newName;
            set
            {
                if (!SetProperty(ref _newName, value))
                    return;

                RaisePropertyChanged(nameof(NameProblem));
                // CanCreate reads the name, so it has to be told too, or the button never enables.
                RaisePropertyChanged(nameof(CanCreate));
                RaisePropertyChanged(nameof(CanTakeLiveSnapshot));
                RaisePropertyChanged(nameof(CanTakeMemorySnapshot));
                RaisePropertyChanged(nameof(CanEnterName));
            }
        }

        /// <summary>Why the typed name cannot be used, or null when it can.</summary>
        public string? NameProblem
        {
            get
            {
                var trimmed = NewName.Trim();
                if (trimmed.Length == 0)
                    return null;   // nothing typed yet is not an error, just nothing to do

                if (Snapshots.Any(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                    return "Un instantané porte déjà ce nom.";

                // What qemu-img can actually store; the rule lives in the core, next to the tool it
                // describes, and is covered by its tests.
                return QemuImgService.ValidateSnapshotName(trimmed);
            }
        }

        public bool CanCreate => CanManage && NewName.Trim().Length > 0 && NameProblem is null;

        private string _status = string.Empty;
        /// <summary>What the section is doing, or why it can do nothing.</summary>
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        public bool HasStatus => Status.Length > 0;

        private IReadOnlyList<DiskConfig> SnapshotableDisks =>
            _config is null ? Array.Empty<DiskConfig>() : VmSnapshotAggregator.SnapshotableDisks(_config);

        /// <summary>Disks that will not be part of any snapshot, so the user is not misled.</summary>
        public string ExcludedDisksText
        {
            get
            {
                if (_config is null)
                    return string.Empty;

                var excluded = VmSnapshotAggregator.UnsupportedDisks(_config);
                return excluded.Count == 0
                    ? string.Empty
                    : "Non couverts, faute de format qcow2 : " +
                      string.Join(", ", excluded.Select(d => $"{d.Id} ({d.Format.ToString().ToLowerInvariant()})"));
            }
        }

        public bool HasExcludedDisks => ExcludedDisksText.Length > 0;

        public void Attach(VmConfig? config)
        {
            _config = config;
            _loaded = false;
            Snapshots.Clear();
            Selected = null;
            NewName = string.Empty;
            Status = string.Empty;

            RaiseAll();
        }

        /// <summary>
        /// Loads the list on first use. Deliberately lazy: reading it runs qemu-img once per disk, and
        /// doing that on every selection change would make arrowing through the inventory sluggish.
        /// </summary>
        public async Task EnsureLoadedAsync()
        {
            if (_loaded || _config is null)
                return;

            await RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            if (_config is null || _manager.DiskService is null)
                return;

            var disks = SnapshotableDisks;
            if (disks.Count == 0)
            {
                Snapshots.Clear();
                Status = _config.Disks.Count == 0
                    ? "Cette machine n'a aucun disque."
                    : "Aucun disque au format qcow2 : seuls ces disques savent porter un instantané interne.";
                _loaded = true;
                RaiseAll();
                return;
            }

            IsBusy = true;
            Status = "Lecture des instantanés…";
            try
            {
                var perDisk = new List<IReadOnlyCollection<DiskSnapshot>>();
                foreach (var disk in disks)
                {
                    var info = await _manager.DiskService.GetInfoAsync(_config.ResolvePath(disk.FileName));
                    perDisk.Add(info.Snapshots);
                }

                var previous = Selected?.Name;
                Snapshots.Clear();

                // Live snapshots are overlay levels, not internal snapshots, so reading the images never
                // finds them; they come from the descriptor's chains instead. One list, newest first.
                var live = VmSnapshotAggregator.LiveSnapshots(
                    _config, file => new DateTimeOffset(File.GetCreationTimeUtc(_config.ResolvePath(file)), TimeSpan.Zero));
                foreach (var snapshot in VmSnapshotAggregator.Aggregate(perDisk).Concat(live).OrderByDescending(s => s.CreatedAt))
                    Snapshots.Add(new SnapshotItem(snapshot));

                Selected = Snapshots.FirstOrDefault(s => s.Name == previous) ?? Snapshots.FirstOrDefault();
                Status = Snapshots.Count == 0 ? "Aucun instantané." : string.Empty;
                _loaded = true;
            }
            catch (Exception ex)
            {
                Status = string.Empty;
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                RaiseAll();
            }
        }

        private VmSession? _session;
        /// <summary>
        /// The live machine, when there is one. Memory and live snapshots go through QEMU rather than
        /// qemu-img, so they need the session and not just the images.
        /// </summary>
        public VmSession? Session
        {
            get => _session;
            set
            {
                if (!SetProperty(ref _session, value))
                    return;

                RaisePropertyChanged(nameof(CanTakeLiveSnapshot));
                RaisePropertyChanged(nameof(CanTakeMemorySnapshot));
                RaisePropertyChanged(nameof(CanEnterName));
                RaisePropertyChanged(nameof(MemorySnapshotUnavailableReason));
                RaisePropertyChanged(nameof(HasMemorySnapshotCaveat));
            }
        }

        /// <summary>
        /// A crash-consistent snapshot can be taken of a running machine on any accelerator: each disk
        /// gains an overlay and writes diverge from that point.
        /// </summary>
        public bool CanTakeLiveSnapshot =>
            Session?.IsRunning == true && !IsBusy && NewName.Trim().Length > 0 && NameProblem is null;

        /// <summary>
        /// Saving the guest's memory needs migration support, which WHPX does not provide, so this is
        /// only ever possible on a machine running under emulation.
        /// </summary>
        public bool CanTakeMemorySnapshot =>
            CanTakeLiveSnapshot && Session?.SupportsMemorySnapshots == true;

        public string MemorySnapshotUnavailableReason =>
            Session?.SupportsMemorySnapshots == true
                ? string.Empty
                : "Enregistrer la mémoire exige le support de migration, absent de WHPX : seule une machine " +
                  "en émulation TCG le permet. Sur cette machine, un instantané à chaud ne capture que les " +
                  "disques, comme une coupure de courant.";

        public bool HasMemorySnapshotCaveat => MemorySnapshotUnavailableReason.Length > 0;

        /// <summary>
        /// Snapshots the running machine including its memory, so restoring resumes exactly there.
        /// </summary>
        public async Task CreateMemorySnapshotAsync()
        {
            if (_config is null || Session is null || !CanTakeMemorySnapshot)
                return;

            var name = NewName.Trim();
            IsBusy = true;
            Status = $"Enregistrement de « {name} » avec la mémoire…";
            try
            {
                await Session.SaveSnapshotAsync(name);
                NewName = string.Empty;
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync();
            }
        }

        /// <summary>
        /// Snapshots the disks of a running machine by giving each one an overlay, and records the chain
        /// in the descriptor so the shell can undo it later.
        /// </summary>
        public async Task CreateLiveSnapshotAsync()
        {
            if (_config is null || Session is null || !CanTakeLiveSnapshot)
                return;

            var name = NewName.Trim();
            IsBusy = true;
            Status = $"Instantané à chaud « {name} »…";
            try
            {
                var overlays = await Session.CreateLiveDiskSnapshotAsync(name);

                foreach (var disk in _config.Disks)
                {
                    if (!overlays.TryGetValue(disk.Id, out var overlay))
                        continue;

                    // The machine is already writing to the overlay, so the descriptor has to follow or
                    // the next start would reopen the old image and lose everything written since. A disk
                    // that already was an overlay keeps its whole chain, so each level can be undone.
                    disk.PushOverlay(Path.GetFileName(overlay));
                }

                NewName = string.Empty;
                _requestSave();
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync();
            }
        }

        public async Task CreateAsync()
        {
            if (_config is null || _manager.DiskService is null || !CanCreate)
                return;

            var name = NewName.Trim();
            IsBusy = true;
            Status = $"Création de « {name} »…";
            try
            {
                foreach (var disk in SnapshotableDisks)
                    await _manager.DiskService.CreateOfflineSnapshotAsync(_config.ResolvePath(disk.FileName), name);

                NewName = string.Empty;
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync();
            }
        }

        /// <summary>
        /// True when the selected snapshot can be resumed rather than merely rolled back: it holds guest
        /// memory and there is a live emulated machine to load it into.
        /// </summary>
        public bool CanResumeSelection =>
            Selected?.Snapshot.HasMemoryState == true
            && !IsBusy
            && Session?.IsRunning == true
            && Session?.SupportsMemorySnapshots == true;

        /// <summary>
        /// Loads a snapshot including its memory into the running machine, which carries on exactly
        /// where it was captured. This is loadvm, not a disk rollback -- the two cannot be substituted:
        /// applying such a snapshot with qemu-img would restore the disk and leave the saved memory
        /// unused, so the guest would come up as if it had lost power mid-run.
        /// </summary>
        public async Task ResumeAsync(SnapshotItem item)
        {
            if (Session is null || !CanResumeSelection)
                return;

            IsBusy = true;
            Status = $"Reprise de « {item.Name} »…";
            try
            {
                await Session.RestoreSnapshotAsync(item.Name);
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                RaiseAll();
            }
        }

        /// <summary>Rolls every disk back to the snapshot. Discards the current disk contents.</summary>
        public async Task RestoreAsync(SnapshotItem item)
        {
            if (_config is null || _manager.DiskService is null || !CanManage)
                return;

            IsBusy = true;
            Status = $"Restauration de « {item.Name} »…";
            try
            {
                if (item.Snapshot.IsLive)
                {
                    await new LiveSnapshotOperations(_manager.DiskService).RevertAsync(_config, item.Name);
                    _requestSave();
                }
                else
                {
                    foreach (var disk in SnapshotableDisks)
                        await _manager.DiskService.ApplyOfflineSnapshotAsync(_config.ResolvePath(disk.FileName), item.Name);
                }
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync();
            }
        }

        public async Task DeleteAsync(SnapshotItem item)
        {
            if (_config is null || _manager.DiskService is null || !CanManage)
                return;

            IsBusy = true;
            Status = $"Suppression de « {item.Name} »…";
            try
            {
                if (item.Snapshot.IsLive)
                {
                    // Keeps what the disks hold now: the overlay is merged downwards, not thrown away.
                    await new LiveSnapshotOperations(_manager.DiskService).MergeAsync(_config, item.Name);
                    _requestSave();
                    return;
                }

                foreach (var disk in SnapshotableDisks)
                {
                    var path = _config.ResolvePath(disk.FileName);
                    try
                    {
                        await _manager.DiskService.DeleteOfflineSnapshotAsync(path, item.Name);
                    }
                    catch (QemuImgException)
                    {
                        // A partial snapshot is missing from some disks; deleting the rest is still the
                        // right outcome, so one absent name must not abort the cleanup.
                    }
                }
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync();
            }
        }

        private void RaiseAll()
        {
            RaisePropertyChanged(nameof(CanManage));
            RaisePropertyChanged(nameof(CanCreate));
            RaisePropertyChanged(nameof(HasStatus));
            RaisePropertyChanged(nameof(NameProblem));
            RaisePropertyChanged(nameof(ExcludedDisksText));
            RaisePropertyChanged(nameof(HasExcludedDisks));
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(CanActOnSelection));
            RaisePropertyChanged(nameof(CanResumeSelection));
            RaisePropertyChanged(nameof(CanTakeLiveSnapshot));
            RaisePropertyChanged(nameof(CanTakeMemorySnapshot));
            RaisePropertyChanged(nameof(CanEnterName));
            RaisePropertyChanged(nameof(MemorySnapshotUnavailableReason));
            RaisePropertyChanged(nameof(HasMemorySnapshotCaveat));
            RaisePropertyChanged(nameof(IsLockedByRunningMachine));
        }
    }
}
