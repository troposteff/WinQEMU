using System.Globalization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Snapshots taken by themselves, as VMware's AutoProtect does: one at most every interval, the oldest
    /// removed beyond the number kept. Only those WinQEMU took ("Auto …") are ever removed.
    /// </summary>
    /// <remarks>
    /// Taken as the machine starts, offline with qemu-img (VmSnapshotAggregator.SnapshotableDisks), not while it
    /// runs: under WHPX the only snapshot of a running machine is a live overlay, and an overlay can only be
    /// merged away with the machine stopped, so snapshots taken every hour of a long run would lengthen the
    /// chain without any way to prune it. A start is also the state the user comes back to.
    ///
    /// No ConfigureAwait(false), as in LiveSnapshotOperations: the lineage is recorded in the descriptor, bound
    /// to the interface, whose PropertyChanged must stay on the calling thread.
    /// </remarks>
    public static class AutoSnapshots
    {
        public const string Prefix = "Auto ";

        /// <summary>The intervals offered, in hours: each start, then at most hourly … weekly.</summary>
        public static readonly IReadOnlyList<int> Intervals = new[] { 0, 1, 6, 12, 24, 168 };

        public static string IntervalLabel(int hours) => hours switch
        {
            0 => CoreText.AutoSnapshotEachStart,
            1 => CoreText.AutoSnapshotHourly,
            168 => CoreText.AutoSnapshotWeekly,
            24 => CoreText.AutoSnapshotDaily,
            _ => string.Format(CultureInfo.CurrentCulture, CoreText.AutoSnapshotEveryHours, hours)
        };

        /// <summary>A name qemu-img keeps as is (ASCII) and that sorts by date.</summary>
        public static string NameFor(DateTime local) =>
            Prefix + local.ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture);

        public static bool IsAutomatic(string name) =>
            name.StartsWith(Prefix, StringComparison.Ordinal) && name.Length == Prefix.Length + 16;

        /// <summary>Whether a snapshot is to be taken at a start now.</summary>
        public static bool IsDue(VmConfig config, DateTime nowUtc) =>
            config.AutoSnapshot
            && VmSnapshotAggregator.SnapshotableDisks(config).Count > 0
            && (config.LastAutoSnapshotUtc is not { } last || nowUtc - last >= TimeSpan.FromHours(config.AutoSnapshotHours));

        /// <summary>
        /// The automatic snapshots beyond the number kept, oldest first. A partial one (absent from a disk)
        /// counts like any other: what it has is removed.
        /// </summary>
        public static IReadOnlyList<string> Surplus(IEnumerable<string> names, int keep) =>
            names.Where(IsAutomatic).Distinct().OrderByDescending(n => n, StringComparer.Ordinal).Skip(Math.Max(1, keep))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Takes the snapshot and removes the surplus, the machine being stopped. Returns the name taken, or
        /// null when none was due.
        /// </summary>
        public static async Task<string?> TakeIfDueAsync(VmConfig config, QemuImgService images, IEnumerable<VmConfig> library,
            DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (!IsDue(config, nowUtc))
                return null;

            var disks = VmSnapshotAggregator.SnapshotableDisks(config);
            var tree = await TreeAsync(config, images, library, cancellationToken);
            var name = NameFor(nowUtc.ToLocalTime());
            if (SnapshotNames(tree).Contains(name))
            {
                config.LastAutoSnapshotUtc = nowUtc;
                return null;
            }

            var parent = SnapshotTree.CurrentParent(tree);
            foreach (var disk in disks)
                await images.CreateOfflineSnapshotAsync(config.ResolvePath(disk.FileName), name, cancellationToken);
            SnapshotTree.RecordTaken(config, name, parent);
            config.LastAutoSnapshotUtc = nowUtc;

            tree = await TreeAsync(config, images, library, cancellationToken);
            foreach (var old in Surplus(SnapshotNames(tree), config.AutoSnapshotKeep))
            {
                var oldParent = SnapshotTree.ParentOf(tree, old);
                foreach (var disk in disks)
                {
                    try
                    {
                        await images.DeleteOfflineSnapshotAsync(config.ResolvePath(disk.FileName), old, cancellationToken);
                    }
                    catch (QemuImgException)
                    {
                        // Absent from this disk (a partial snapshot): nothing to remove there.
                    }
                }
                SnapshotTree.RecordDeleted(config, old, oldParent);
            }
            return name;
        }

        private static async Task<SnapshotTreeNode> TreeAsync(VmConfig config, QemuImgService images, IEnumerable<VmConfig> library,
            CancellationToken cancellationToken)
        {
            var perDisk = new List<IReadOnlyCollection<DiskSnapshot>>();
            foreach (var disk in VmSnapshotAggregator.SnapshotableDisks(config))
                perDisk.Add((await images.GetInfoAsync(config.ResolvePath(disk.FileName), cancellationToken)).Snapshots);
            var live = VmSnapshotAggregator.LiveSnapshots(
                config, file => new DateTimeOffset(File.GetCreationTimeUtc(config.ResolvePath(file)), TimeSpan.Zero));
            return SnapshotTree.Build(config, VmSnapshotAggregator.Aggregate(perDisk).Concat(live).ToList(), library);
        }

        private static List<string> SnapshotNames(SnapshotTreeNode root)
        {
            var names = new List<string>();
            void Walk(SnapshotTreeNode node)
            {
                if (node.Snapshot is { IsLive: false } snapshot)
                    names.Add(snapshot.Name);
                foreach (var child in node.Children)
                    Walk(child);
            }
            Walk(root);
            return names;
        }
    }
}
