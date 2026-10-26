using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// A snapshot of a whole machine, assembled from the per-image snapshots that share its name.
    /// </summary>
    /// <remarks>
    /// A machine with two disks takes two qcow2 snapshots under one name, and the pair only means
    /// anything together. <see cref="DiskSnapshot"/> is what one image reports; this is what the machine
    /// has.
    /// </remarks>
    public sealed record VmSnapshot(
        string Name,
        DateTimeOffset CreatedAt,
        int PresentOnDisks,
        int TotalDisks,
        long VmStateSizeBytes,
        bool IsLive = false)
    {
        /// <summary>
        /// True when the snapshot is missing from some of the machine's disks. Restoring it would put
        /// those disks back while the others stay where they are, leaving the set inconsistent.
        /// </summary>
        public bool IsPartial => PresentOnDisks < TotalDisks;

        /// <summary>True when the guest's memory was saved too, which only TCG machines can do.</summary>
        public bool HasMemoryState => VmStateSizeBytes > 0;

        // IsLive: a live disk snapshot, i.e. one level of each disk's overlay chain rather than a qcow2
        // internal snapshot. Undone by unstacking overlays, never by qemu-img snapshot -a.
    }

    /// <summary>
    /// Turns per-disk snapshot listings into the machine-level view, and decides which disks can take
    /// part at all.
    /// </summary>
    public static class VmSnapshotAggregator
    {
        /// <summary>
        /// Disks that can hold an internal snapshot. Only qcow2 has the feature; the other formats have
        /// nowhere to put one.
        /// </summary>
        public static IReadOnlyList<DiskConfig> SnapshotableDisks(VmConfig config) =>
            config.Disks.Where(d => d.Format == DiskFormat.Qcow2).ToList();

        /// <summary>
        /// Disks that no snapshot will cover. Surfacing these matters: a machine whose data disk is raw
        /// would otherwise appear fully protected by a snapshot that never touched it.
        /// </summary>
        public static IReadOnlyList<DiskConfig> UnsupportedDisks(VmConfig config) =>
            config.Disks.Where(d => d.Format != DiskFormat.Qcow2).ToList();

        /// <summary>
        /// Groups the snapshots of several disks into machine-level snapshots, newest first.
        /// </summary>
        /// <param name="snapshotsPerDisk">
        /// One entry per participating disk, each holding what that image reported. The number of
        /// entries is the number of disks a complete snapshot has to span.
        /// </param>
        public static IReadOnlyList<VmSnapshot> Aggregate(
            IEnumerable<IReadOnlyCollection<DiskSnapshot>> snapshotsPerDisk)
        {
            var perDisk = snapshotsPerDisk.ToList();
            var totalDisks = perDisk.Count;

            var grouped = new Dictionary<string, List<DiskSnapshot>>(StringComparer.Ordinal);
            foreach (var disk in perDisk)
            {
                // Deduplicated within one image: two entries of the same name on the same disk would
                // otherwise inflate the count and make a complete snapshot look over-covered.
                foreach (var snapshot in disk.GroupBy(s => s.Name, StringComparer.Ordinal).Select(g => g.First()))
                {
                    if (!grouped.TryGetValue(snapshot.Name, out var list))
                        grouped[snapshot.Name] = list = new List<DiskSnapshot>();
                    list.Add(snapshot);
                }
            }

            return grouped
                // Newest first, by the most recent image in the group: that is the order a user reads a
                // history in, and it is stable because ties keep their insertion order.
                .OrderByDescending(entry => entry.Value.Max(s => s.CreatedAt))
                .Select(entry => new VmSnapshot(
                    entry.Key,
                    // The oldest of the group is when the snapshot was taken; the later ones are just
                    // the remaining disks being written.
                    entry.Value.Min(s => s.CreatedAt),
                    entry.Value.Count,
                    totalDisks,
                    entry.Value.Max(s => s.VmStateSizeBytes)))
                .ToList();
        }

        /// <summary>
        /// The live disk snapshots of a machine: each overlay level of each disk, grouped by snapshot name.
        /// </summary>
        /// <remarks>
        /// A live snapshot is not stored inside an image the way an internal one is: it is the overlay the
        /// disk started writing to at that moment, named <c>&lt;disk id&gt;-&lt;snapshot name&gt;.qcow2</c>
        /// by <see cref="VmSession.CreateLiveDiskSnapshotAsync"/>. Reading the internal snapshots of the
        /// images therefore never finds them, which is why they used to be missing from the list.
        /// </remarks>
        /// <param name="createdAt">When an overlay file was created, given its descriptor-relative name.</param>
        public static IReadOnlyList<VmSnapshot> LiveSnapshots(VmConfig config, Func<string, DateTimeOffset> createdAt)
        {
            var disks = SnapshotableDisks(config);
            var grouped = new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
            foreach (var disk in disks)
            {
                var chain = disk.ChainFromTop;
                // Every image but the last is an overlay; the last is the disk as it was first created.
                foreach (var overlay in chain.Take(chain.Count - 1))
                {
                    var name = DiskConfig.SnapshotNameOf(disk.Id, overlay);
                    if (!grouped.TryGetValue(name, out var times))
                        grouped[name] = times = new List<DateTimeOffset>();
                    times.Add(createdAt(overlay));
                }
            }

            return grouped
                .OrderByDescending(entry => entry.Value.Max())
                .Select(entry => new VmSnapshot(
                    entry.Key, entry.Value.Min(), entry.Value.Count, disks.Count, 0, IsLive: true))
                .ToList();
        }
    }
}
