using System.Text.Json.Serialization;

namespace WinQEMU.Core.Models
{
    public class DiskConfig : ObservableBase
    {
        private string _id = "disk0";
        /// <summary>Stable identifier used as the QEMU node id. Must be unique within a VM.</summary>
        public string Id { get => _id; set => SetProperty(ref _id, value); }

        private string _fileName = string.Empty;
        /// <summary>
        /// Path to the image. Relative paths are resolved against the VM folder, which is
        /// what makes a VM folder portable.
        /// </summary>
        public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }

        private DiskFormat _format = DiskFormat.Qcow2;
        public DiskFormat Format { get => _format; set => SetProperty(ref _format, value); }

        private DiskBus _bus = DiskBus.VirtioBlk;
        public DiskBus Bus { get => _bus; set => SetProperty(ref _bus, value); }

        private long _capacityBytes;
        /// <summary>Provisioned (virtual) size. Informational once the image exists.</summary>
        public long CapacityBytes { get => _capacityBytes; set => SetProperty(ref _capacityBytes, value); }

        private DiskCacheMode _cache = DiskCacheMode.Writeback;
        public DiskCacheMode Cache { get => _cache; set => SetProperty(ref _cache, value); }

        private bool _readOnly;
        public bool ReadOnly { get => _readOnly; set => SetProperty(ref _readOnly, value); }

        private bool _discard = true;
        /// <summary>Pass guest TRIM through to the image so qcow2 files can shrink.</summary>
        public bool Discard { get => _discard; set => SetProperty(ref _discard, value); }

        private string _backingFileName = string.Empty;
        /// <summary>
        /// The image this one was layered on top of, when the disk is a live-snapshot overlay; empty
        /// otherwise. Recorded rather than probed: the relationship is created by taking the snapshot, so
        /// it is known for certain at that moment, and writing it down means the chain survives a reload
        /// without running qemu-img on every disk to rediscover it.
        /// </summary>
        public string BackingFileName { get => _backingFileName; set => SetProperty(ref _backingFileName, value); }

        /// <summary>
        /// The images further down the chain, nearest first, when overlays were stacked by taking several
        /// live snapshots in a row. Empty for a disk with one overlay or none.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="BackingFileName"/> so descriptors written before chains were tracked
        /// still load as they are. Without it, reverting the top overlay left the one underneath with no
        /// recorded base: still an overlay on disk, but no longer one the application would offer to undo.
        /// </remarks>
        public List<string> DeeperBackingFiles { get; set; } = new();

        [JsonIgnore]
        public bool IsOverlay => !string.IsNullOrWhiteSpace(BackingFileName);

        /// <summary>How many overlays sit on top of the original image; 0 when the disk is not an overlay.</summary>
        [JsonIgnore]
        public int OverlayDepth => IsOverlay ? 1 + DeeperBackingFiles.Count : 0;

        /// <summary>
        /// Records that the disk now writes to <paramref name="overlayFileName"/>, layered on the image it
        /// used until now — which may itself be an overlay.
        /// </summary>
        public void PushOverlay(string overlayFileName)
        {
            if (IsOverlay)
                DeeperBackingFiles.Insert(0, BackingFileName);

            BackingFileName = FileName;
            FileName = overlayFileName;
            RaiseOverlayChanged();
        }

        /// <summary>
        /// Takes the top overlay off the record: the disk goes back to the image underneath, and the next
        /// image down becomes its base. Returns the overlay that was removed, which the caller deletes or
        /// has already committed. Does nothing to files.
        /// </summary>
        public string PopOverlay()
        {
            if (!IsOverlay)
                throw new InvalidOperationException($"Disk '{Id}' is not an overlay.");

            var overlay = FileName;
            FileName = BackingFileName;
            if (DeeperBackingFiles.Count > 0)
            {
                BackingFileName = DeeperBackingFiles[0];
                DeeperBackingFiles.RemoveAt(0);
            }
            else
            {
                BackingFileName = string.Empty;
            }

            RaiseOverlayChanged();
            return overlay;
        }

        /// <summary>
        /// The images of the disk from the one it writes to down to the original, e.g.
        /// [disk0-b.qcow2, disk0-a.qcow2, disk0.qcow2]. A single entry when the disk is not an overlay.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<string> ChainFromTop =>
            IsOverlay
                ? new[] { FileName, BackingFileName }.Concat(DeeperBackingFiles).ToList()
                : new[] { FileName };

        /// <summary>
        /// Takes one overlay out of the chain, wherever it sits — the caller has already merged it into
        /// the image below and pointed the image above at that image. Does nothing to files.
        /// </summary>
        public void RemoveOverlay(string overlayFileName)
        {
            var chain = ChainFromTop.ToList();
            var index = chain.IndexOf(overlayFileName);
            if (index < 0 || index == chain.Count - 1)
                throw new InvalidOperationException($"'{overlayFileName}' is not an overlay of disk '{Id}'.");

            chain.RemoveAt(index);
            FileName = chain[0];
            BackingFileName = chain.Count > 1 ? chain[1] : string.Empty;
            DeeperBackingFiles = chain.Skip(2).ToList();
            RaiseOverlayChanged();
        }

        /// <summary>
        /// The snapshot an overlay file stands for: "disk0-Test 1.qcow2" is snapshot "Test 1" of disk0.
        /// Falls back to the bare file name for an overlay that does not follow that pattern.
        /// </summary>
        public static string SnapshotNameOf(string diskId, string overlayFileName)
        {
            var name = Path.GetFileNameWithoutExtension(overlayFileName);
            var prefix = diskId + "-";
            return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
                ? name[prefix.Length..]
                : name;
        }

        private void RaiseOverlayChanged()
        {
            RaisePropertyChanged(nameof(DeeperBackingFiles));
            RaisePropertyChanged(nameof(IsOverlay));
            RaisePropertyChanged(nameof(OverlayDepth));
        }

        private string _linkedBaseFileName = string.Empty;
        /// <summary>
        /// For a linked clone: the image of another machine that the bottom of this disk's chain was
        /// created on, and still reads everything it has not overwritten from. Empty otherwise. Relative to
        /// this machine's folder when both sit on the same volume, so moving the whole library keeps the
        /// pair working.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="BackingFileName"/>: that one marks an overlay the application may
        /// unstack or merge, and doing either here would write into an image other machines depend on.
        /// Recorded so the original can be stopped from changing the image (<see cref="Services.LinkedCloneGuard"/>).
        /// </remarks>
        public string LinkedBaseFileName
        {
            get => _linkedBaseFileName;
            set
            {
                if (SetProperty(ref _linkedBaseFileName, value))
                    RaisePropertyChanged(nameof(IsLinkedClone));
            }
        }

        [JsonIgnore]
        public bool IsLinkedClone => !string.IsNullOrWhiteSpace(LinkedBaseFileName);

        private int _bootIndex = -1;
        /// <summary>Boot order position, or -1 to leave it unspecified.</summary>
        public int BootIndex { get => _bootIndex; set => SetProperty(ref _bootIndex, value); }

        [JsonIgnore]
        public double CapacityGb => CapacityBytes / 1024d / 1024d / 1024d;
    }
}
