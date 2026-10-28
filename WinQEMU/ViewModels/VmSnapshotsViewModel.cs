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
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Presentation wrapper over a <see cref="VmSnapshot"/>. Only the wording lives here; what a
    /// snapshot is, which disks it spans and whether it is partial are decided in the core.
    /// </summary>
    public sealed class SnapshotItem
    {
        public SnapshotItem(VmSnapshot snapshot, string? lockReason = null)
        {
            Snapshot = snapshot;
            LockReason = lockReason;
        }

        public VmSnapshot Snapshot { get; }

        /// <summary>
        /// Why the snapshot cannot be deleted — a linked clone reads its level or one under it — or null.
        /// Restoring it stays possible: the clone's images are then kept.
        /// </summary>
        public string? LockReason { get; }

        public bool IsLocked => LockReason is not null;

        public string Name => Snapshot.Name;

        public bool IsPartial => Snapshot.IsPartial;

        public string CreatedText =>
            Snapshot.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        public string DetailText
        {
            get
            {
                var parts = new List<string>
                {
                    Snapshot.IsLive
                        ? DialogText.SnapshotsLiveDisks
                        : Snapshot.HasMemoryState
                            ? string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsMemory, Sizes.Format(Snapshot.VmStateSizeBytes))
                            : DialogText.SnapshotsDiskState
                };

                // Only worth saying when the machine has more than one disk to be inconsistent about.
                if (Snapshot.TotalDisks > 1)
                    parts.Add(string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsDiskCount, Snapshot.PresentOnDisks, Snapshot.TotalDisks));

                if (IsLocked)
                    parts.Add(DialogText.SnapshotsCloneBase);

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

        private SnapshotTreeNode? _tree;
        /// <summary>
        /// The snapshots as a tree, rebuilt with the list: lineage, "Vous êtes ici" and linked clones
        /// (see SnapshotTree). Null while nothing has been read.
        /// </summary>
        public SnapshotTreeNode? Tree { get => _tree; private set => SetProperty(ref _tree, value); }

        /// <summary>Where a snapshot taken now would hang from: where the machine stands.</summary>
        private string CurrentParent => Tree is { } tree ? SnapshotTree.CurrentParent(tree) : _config?.CurrentSnapshot ?? string.Empty;

        /// <summary>Records lineage in the descriptor, and saves it.</summary>
        private void Record(Action<VmConfig> change)
        {
            if (_config is null)
                return;
            change(_config);
            _requestSave();
        }

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
                RaisePropertyChanged(nameof(CanDeleteSelection));
                RaisePropertyChanged(nameof(CanResumeSelection));
                RaisePropertyChanged(nameof(SelectedLockReason));
                RaisePropertyChanged(nameof(IsSelectionLocked));
            }
        }

        public bool HasSelection => Selected is not null;

        /// <summary>Shown under the buttons, so a disabled Supprimer says why rather than just greying out.</summary>
        public string SelectedLockReason => Selected?.LockReason ?? string.Empty;

        public bool IsSelectionLocked => Selected?.IsLocked == true;

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
                RaisePropertyChanged(nameof(CanDeleteSelection));
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
                RaisePropertyChanged(nameof(CanDeleteSelection));
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

        /// <summary>
        /// Deleting merges a level into the one below, which writes into an image a linked clone may read;
        /// restoring never does, so only Supprimer is tied to the lock.
        /// </summary>
        public bool CanDeleteSelection => CanActOnSelection && !IsSelectionLocked;

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
                    return DialogText.SnapshotsNameTaken;

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
                    : string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsExcluded,
                        string.Join(", ", excluded.Select(d => $"{d.Id} ({d.Format.ToString().ToLowerInvariant()})")));
            }
        }

        public bool HasExcludedDisks => ExcludedDisksText.Length > 0;

        /// <summary>
        /// Bumped on every <see cref="Attach"/>. A read started for the previous machine compares it on
        /// return and drops its result: qemu-img answers after the user may have moved on, and its list
        /// then appeared under the next machine selected.
        /// </summary>
        private int _generation;

        /// <summary>How long the selection has to rest on a machine before an open section reads its list.</summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

        public void Attach(VmConfig? config)
        {
            var generation = ++_generation;
            _config = config;
            _loaded = false;
            IsBusy = false;
            Snapshots.Clear();
            Tree = null;
            Selected = null;
            NewName = string.Empty;
            Status = string.Empty;

            RaiseAll();

            // Expanding is what loads the list, and an open section is never expanded again: without this
            // it stayed empty, or kept nothing at all, for every machine selected after the first.
            if (config is not null && IsSectionExpanded)
                _ = LoadWhenSettledAsync(generation);
        }

        private bool _isSectionExpanded;
        /// <summary>Bound to the section's expander; opening it loads the list.</summary>
        public bool IsSectionExpanded
        {
            get => _isSectionExpanded;
            set
            {
                if (SetProperty(ref _isSectionExpanded, value) && value)
                    _ = EnsureLoadedAsync();
            }
        }

        /// <summary>
        /// Reads the list once the selection has rested. Arrowing through the inventory with the section
        /// open attaches every machine on the way; only the one the user stops on is worth a qemu-img run.
        /// </summary>
        private async Task LoadWhenSettledAsync(int generation)
        {
            await Task.Delay(SettleDelay);
            if (generation == _generation && IsSectionExpanded)
                await EnsureLoadedAsync();
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
            // Captured, not re-read after each await: the selection can change while qemu-img runs.
            var config = _config;
            var generation = _generation;
            if (config is null || _manager.DiskService is null)
                return;

            var disks = SnapshotableDisks;
            if (disks.Count == 0)
            {
                Snapshots.Clear();
                Tree = null;
                Status = config.Disks.Count == 0
                    ? DialogText.SnapshotsNoDisk
                    : DialogText.SnapshotsNoQcow2;
                _loaded = true;
                RaiseAll();
                return;
            }

            IsBusy = true;
            Status = DialogText.SnapshotsReading;
            try
            {
                var perDisk = new List<IReadOnlyCollection<DiskSnapshot>>();
                foreach (var disk in disks)
                {
                    var info = await _manager.DiskService.GetInfoAsync(config.ResolvePath(disk.FileName));
                    if (generation != _generation)
                        return;
                    perDisk.Add(info.Snapshots);
                }

                var previous = Selected?.Name;
                Snapshots.Clear();

                // Live snapshots are overlay levels, not internal snapshots, so reading the images never
                // finds them; they come from the descriptor's chains instead. One list, newest first.
                var live = VmSnapshotAggregator.LiveSnapshots(
                    config, file => new DateTimeOffset(File.GetCreationTimeUtc(config.ResolvePath(file)), TimeSpan.Zero));
                var all = VmSnapshotAggregator.Aggregate(perDisk).Concat(live).ToList();
                Tree = SnapshotTree.Build(config, all, _manager.VirtualMachines.ToList());
                foreach (var snapshot in all.OrderByDescending(s => s.CreatedAt))
                {
                    // Only live snapshots are levels of the chain a clone can rest on; internal ones live
                    // inside the image the machine writes to, which is never a clone's base.
                    var lockReason = snapshot.IsLive ? _manager.WhyLiveSnapshotCannotBeDeleted(config, snapshot.Name) : null;
                    Snapshots.Add(new SnapshotItem(snapshot, lockReason));
                }

                Selected = Snapshots.FirstOrDefault(s => s.Name == previous) ?? Snapshots.FirstOrDefault();
                Status = Snapshots.Count == 0 ? DialogText.SnapshotsNone : string.Empty;
                _loaded = true;
            }
            catch (Exception ex)
            {
                if (generation != _generation)
                    return;
                Status = string.Empty;
                _reportError(ex);
            }
            finally
            {
                // The machine selected since owns the busy flag and the status now.
                if (generation == _generation)
                {
                    IsBusy = false;
                    RaiseAll();
                }
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
                : DialogText.SnapshotsNoMemory;

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
            Status = string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsSavingMemory, name);
            try
            {
                var parent = CurrentParent;
                await Session.SaveSnapshotAsync(name);
                Record(c => SnapshotTree.RecordTaken(c, name, parent));
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
            Status = string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsTakingLive, name);
            try
            {
                var parent = CurrentParent;
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
                Record(c => SnapshotTree.RecordTaken(c, name, parent));
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
            Status = string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsCreating, name);
            try
            {
                var parent = CurrentParent;
                var images = await _manager.ImagesForAsync(_config);
                foreach (var disk in SnapshotableDisks)
                    await images.CreateOfflineSnapshotAsync(_config.ResolvePath(disk.FileName), name);

                Record(c => SnapshotTree.RecordTaken(c, name, parent));
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
            Status = string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsResuming, item.Name);
            try
            {
                await Session.RestoreSnapshotAsync(item.Name);
                Record(c => SnapshotTree.RecordRestored(c, item.Name));
                Status = string.Empty;
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

        /// <summary>Rolls every disk back to the snapshot. Discards the current disk contents.</summary>
        public async Task RestoreAsync(SnapshotItem item)
        {
            if (_config is null || _manager.DiskService is null || !CanManage)
                return;

            IsBusy = true;
            Status = string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsRestoring, item.Name);
            try
            {
                var images = await _manager.ImagesForAsync(_config);
                if (item.Snapshot.IsLive)
                {
                    // Images a linked clone reads are kept rather than deleted, and the machine restarts from
                    // the snapshot on a fresh layer. The protection is read now, not when the list was.
                    await new LiveSnapshotOperations(images).RevertAsync(_config, item.Name, _manager.ProtectedImages);
                    // The chain now says where the machine stands: on the snapshot again when a protected
                    // layer made it start a fresh one, or on the snapshot before when the overlay was unstacked.
                    Record(c => c.CurrentSnapshot = string.Empty);
                }
                else
                {
                    foreach (var disk in SnapshotableDisks)
                        await images.ApplyOfflineSnapshotAsync(_config.ResolvePath(disk.FileName), item.Name);
                    Record(c => SnapshotTree.RecordRestored(c, item.Name));
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
            Status = string.Format(CultureInfo.CurrentCulture, DialogText.SnapshotsDeleting, item.Name);
            var parent = Tree is { } tree ? SnapshotTree.ParentOf(tree, item.Name) : string.Empty;
            try
            {
                var images = await _manager.ImagesForAsync(_config);
                if (item.Snapshot.IsLive)
                {
                    // Asked again rather than trusting the item: a clone may have been made since the list was read.
                    if (_manager.WhyLiveSnapshotCannotBeDeleted(_config, item.Name) is { } reason)
                        throw new InvalidOperationException(reason);

                    // Keeps what the disks hold now: the overlay is merged downwards, not thrown away.
                    await new LiveSnapshotOperations(images).MergeAsync(_config, item.Name);
                    Record(c => SnapshotTree.RecordDeleted(c, item.Name, parent));
                    return;
                }

                foreach (var disk in SnapshotableDisks)
                {
                    var path = _config.ResolvePath(disk.FileName);
                    try
                    {
                        await images.DeleteOfflineSnapshotAsync(path, item.Name);
                    }
                    catch (QemuImgException)
                    {
                        // A partial snapshot is missing from some disks; deleting the rest is still the
                        // right outcome, so one absent name must not abort the cleanup.
                    }
                }
                Record(c => SnapshotTree.RecordDeleted(c, item.Name, parent));
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
            RaisePropertyChanged(nameof(CanDeleteSelection));
            RaisePropertyChanged(nameof(CanResumeSelection));
            RaisePropertyChanged(nameof(CanTakeLiveSnapshot));
            RaisePropertyChanged(nameof(CanTakeMemorySnapshot));
            RaisePropertyChanged(nameof(CanEnterName));
            RaisePropertyChanged(nameof(MemorySnapshotUnavailableReason));
            RaisePropertyChanged(nameof(HasMemorySnapshotCaveat));
            RaisePropertyChanged(nameof(IsLockedByRunningMachine));
            RaisePropertyChanged(nameof(SelectedLockReason));
            RaisePropertyChanged(nameof(IsSelectionLocked));
        }
    }
}
