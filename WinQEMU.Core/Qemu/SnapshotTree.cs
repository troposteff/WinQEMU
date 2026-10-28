using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Qemu
{
    public enum SnapshotNodeKind
    {
        /// <summary>The machine as it was before any snapshot.</summary>
        Origin,
        Snapshot,
        /// <summary>The machine's current state, "Vous êtes ici".</summary>
        Current,
        /// <summary>A linked clone, hanging from the state it reads.</summary>
        LinkedClone
    }

    /// <summary>One box of the snapshot tree, with its place in the drawing.</summary>
    public sealed class SnapshotTreeNode
    {
        public SnapshotTreeNode(SnapshotNodeKind kind, string title, VmSnapshot? snapshot = null)
        {
            Kind = kind;
            Title = title;
            Snapshot = snapshot;
        }

        public SnapshotNodeKind Kind { get; }

        public string Title { get; }

        /// <summary>The snapshot, for <see cref="SnapshotNodeKind.Snapshot"/> nodes.</summary>
        public VmSnapshot? Snapshot { get; }

        /// <summary>A live snapshot out of the machine's chain, kept on disk because a linked clone reads it.</summary>
        public bool IsRetained { get; init; }

        /// <summary>For a linked clone: the clone itself.</summary>
        public VmConfig? Clone { get; init; }

        public List<SnapshotTreeNode> Children { get; } = new();

        /// <summary>Depth from the origin: the drawing's column.</summary>
        public int Column { get; internal set; }

        /// <summary>The drawing's row: each branch gets its own, a node shares the row of its first child.</summary>
        public int Row { get; internal set; }

        public IEnumerable<SnapshotTreeNode> SelfAndDescendants() =>
            new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));
    }

    /// <summary>
    /// The snapshots of a machine as a tree, as VMware's snapshot manager draws it: the original state, the
    /// snapshots each under the one it was taken from, "Vous êtes ici", and the linked clones under the state
    /// they read.
    /// </summary>
    /// <remarks>
    /// Two sources of lineage. Live snapshots are overlay levels: the overlay of snapshot S rests on the
    /// overlay of the snapshot before it, so the chain itself says who descends from whom, and the overlay
    /// the machine writes to says where it is. Internal qcow2 snapshots carry no parent at all, so WinQEMU
    /// records one when it takes a snapshot (<see cref="VmConfig.SnapshotLinks"/>), and where the machine
    /// stands after a restore (<see cref="VmConfig.CurrentSnapshot"/>). A recorded parent wins when it still
    /// exists; snapshots older than the record hang from the original state.
    ///
    /// Images kept for a linked clone after a restore (<see cref="DiskConfig.RetainedImages"/>) left the
    /// chain and the list of snapshots; here they come back, as branches the clones hang from.
    /// </remarks>
    public static class SnapshotTree
    {
        /// <summary>The tree of one machine. <paramref name="library"/> is every machine listed, for its linked clones.</summary>
        public static SnapshotTreeNode Build(VmConfig config, IReadOnlyList<VmSnapshot> snapshots, IEnumerable<VmConfig> library)
        {
            var origin = new SnapshotTreeNode(SnapshotNodeKind.Origin, PlatformText.SnapshotTreeOrigin);
            var nodes = new Dictionary<string, SnapshotTreeNode>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots.OrderBy(s => s.CreatedAt))
                nodes.TryAdd(snapshot.Name, new SnapshotTreeNode(SnapshotNodeKind.Snapshot, snapshot.Name, snapshot));

            // The structure of the disks, read from the system disk: every disk of a machine takes the
            // same live snapshots, so one chain tells it all. Overlay O of snapshot S holds what was written
            // after S, on top of the image S's state is; the machine writes into the top one.
            var disk = config.Disks.FirstOrDefault(d => d.Format == DiskFormat.Qcow2);
            var structuralParent = new Dictionary<string, string>(StringComparer.Ordinal);
            var snapshotOfOverlay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // overlay → its snapshot
            var snapshotResting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // image → snapshot whose overlay rests on it
            var structuralCurrent = string.Empty;
            if (disk is not null)
            {
                var chain = disk.ChainFromTop.Select(config.ResolvePath).ToList();
                for (var i = 0; i < chain.Count - 1; i++)
                {
                    var name = DiskConfig.SnapshotNameOf(disk.Id, chain[i]);
                    snapshotOfOverlay[chain[i]] = name;
                    snapshotResting[chain[i + 1]] = name;
                    structuralParent[name] = i + 1 < chain.Count - 1 ? DiskConfig.SnapshotNameOf(disk.Id, chain[i + 1]) : string.Empty;
                }
                if (chain.Count > 1)
                    structuralCurrent = DiskConfig.SnapshotNameOf(disk.Id, chain[0]);

                // Kept for a clone after a restore: out of the chain and of the list of snapshots, back here.
                foreach (var retained in disk.RetainedImages)
                {
                    var path = config.ResolvePath(retained.FileName);
                    var name = DiskConfig.SnapshotNameOf(disk.Id, retained.FileName);
                    var key = RetainedKey(name);
                    var created = File.Exists(path) ? new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero) : DateTimeOffset.MinValue;
                    nodes.TryAdd(key, new SnapshotTreeNode(SnapshotNodeKind.Snapshot, name, new VmSnapshot(name, created, 1, 1, 0, IsLive: true)) { IsRetained = true });
                    snapshotOfOverlay[path] = key;
                    snapshotResting.TryAdd(config.ResolvePath(retained.BackingFileName), key);
                }
                foreach (var retained in disk.RetainedImages)
                {
                    var key = snapshotOfOverlay[config.ResolvePath(retained.FileName)];
                    structuralParent[key] = snapshotOfOverlay.GetValueOrDefault(config.ResolvePath(retained.BackingFileName), string.Empty);
                }
            }

            var recorded = config.SnapshotLinks
                .GroupBy(l => l.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Parent, StringComparer.Ordinal);

            string RawParent(string name) =>
                recorded.TryGetValue(name, out var parent) ? parent : structuralParent.GetValueOrDefault(name, string.Empty);

            // Whether walking up from candidate reaches name: a recorded lineage that loops (a descriptor
            // edited by hand) must not hang the drawing.
            bool Reaches(string candidate, string name)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var walk = candidate; walk.Length > 0 && seen.Add(walk); walk = RawParent(walk))
                {
                    if (walk == name)
                        return true;
                }
                return false;
            }

            // A recorded parent that still exists wins; then the chain; then the original state.
            string ParentOf(string name)
            {
                if (recorded.TryGetValue(name, out var parent) && (parent.Length == 0 || nodes.ContainsKey(parent)) && !Reaches(parent, name))
                    return parent;
                return structuralParent.TryGetValue(name, out var structural) && (structural.Length == 0 || nodes.ContainsKey(structural))
                       && !Reaches(structural, name)
                    ? structural
                    : string.Empty;
            }

            SnapshotTreeNode NodeOf(string name) => name.Length > 0 && nodes.TryGetValue(name, out var node) ? node : origin;

            foreach (var (name, node) in nodes)
                NodeOf(ParentOf(name)).Children.Add(node);

            // Where the machine stands: the last snapshot taken or restored, if still there, or the
            // closest surviving ancestor of it; else the snapshot whose overlay it writes to.
            var current = CurrentFrom(config.CurrentSnapshot, nodes, recorded) ?? structuralCurrent;
            NodeOf(current).Children.Add(new SnapshotTreeNode(SnapshotNodeKind.Current, PlatformText.SnapshotTreeYouAreHere));

            // Linked clones read the whole of an image, i.e. the state when it stopped being written: the
            // snapshot whose overlay rests on it ("Base de <clone>"), or, when nothing rests on it any more
            // (a kept branch), the snapshot it belongs to.
            foreach (var other in library)
            {
                if (ReferenceEquals(other, config) || other.Disks.FirstOrDefault(d => d.IsLinkedClone) is not { } cloneDisk)
                    continue;
                var basePath = other.ResolvePath(cloneDisk.LinkedBaseFileName);
                var state = snapshotResting.TryGetValue(basePath, out var resting) ? resting
                    : snapshotOfOverlay.TryGetValue(basePath, out var own) ? own
                    : null;
                if (state is null && (disk is null || !string.Equals(basePath, config.ResolvePath(disk.ChainFromTop[^1]), StringComparison.OrdinalIgnoreCase)))
                    continue;
                NodeOf(state ?? string.Empty).Children.Add(new SnapshotTreeNode(SnapshotNodeKind.LinkedClone, other.Name) { Clone = other });
            }

            foreach (var node in origin.SelfAndDescendants())
                node.Children.Sort(ChildOrder);
            Layout(origin);
            return origin;
        }

        private static string RetainedKey(string name) => "\u0001retained:" + name;

        private static string? CurrentFrom(string current, Dictionary<string, SnapshotTreeNode> nodes, Dictionary<string, string> recorded)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var walk = current; walk.Length > 0 && seen.Add(walk); walk = recorded.GetValueOrDefault(walk, string.Empty))
            {
                if (nodes.TryGetValue(walk, out var node) && !node.IsRetained)
                    return walk;
            }
            return current.Length > 0 && recorded.ContainsKey(current) ? string.Empty : null;
        }

        /// <summary>Snapshots by date, then the current state, then clones: the main line stays on top.</summary>
        private static int ChildOrder(SnapshotTreeNode a, SnapshotTreeNode b)
        {
            static int Rank(SnapshotTreeNode n) => n.Kind switch
            {
                SnapshotNodeKind.Snapshot => n.IsRetained ? 2 : 0,
                SnapshotNodeKind.Current => 1,
                _ => 3
            };
            var byKind = Rank(a).CompareTo(Rank(b));
            if (byKind != 0)
                return byKind;
            return (a.Snapshot?.CreatedAt ?? DateTimeOffset.MaxValue).CompareTo(b.Snapshot?.CreatedAt ?? DateTimeOffset.MaxValue);
        }

        /// <summary>
        /// Left to right by depth, as VMware draws it. Each leaf gets a row of its own; a node takes the row
        /// of its first child, so a line of snapshots is drawn straight.
        /// </summary>
        public static void Layout(SnapshotTreeNode root)
        {
            var nextRow = 0;
            void Place(SnapshotTreeNode node, int column)
            {
                node.Column = column;
                if (node.Children.Count == 0)
                {
                    node.Row = nextRow++;
                    return;
                }
                foreach (var child in node.Children)
                    Place(child, column + 1);
                node.Row = node.Children[0].Row;
            }
            Place(root, 0);
        }

        // ---- Recording lineage, as snapshots are taken, restored and deleted ----

        /// <summary>A snapshot was taken: it comes from where the machine stood, and the machine now stands on it.</summary>
        public static void RecordTaken(VmConfig config, string name, string parent)
        {
            config.SnapshotLinks.RemoveAll(l => l.Name == name);
            config.SnapshotLinks.Add(new SnapshotLink { Name = name, Parent = parent == name ? string.Empty : parent });
            config.CurrentSnapshot = name;
        }

        /// <summary>A snapshot was restored or resumed: the machine stands on it again.</summary>
        public static void RecordRestored(VmConfig config, string name) => config.CurrentSnapshot = name;

        /// <summary>A snapshot is gone: what came from it now comes from its parent, the machine included.</summary>
        public static void RecordDeleted(VmConfig config, string name, string parent)
        {
            foreach (var link in config.SnapshotLinks.Where(l => l.Parent == name))
                link.Parent = parent;
            config.SnapshotLinks.RemoveAll(l => l.Name == name);
            if (config.CurrentSnapshot == name)
                config.CurrentSnapshot = parent;
        }

        /// <summary>The parent the tree shows for a snapshot, "" for the original state.</summary>
        public static string ParentOf(SnapshotTreeNode root, string name)
        {
            foreach (var node in root.SelfAndDescendants())
            {
                if (node.Children.Any(c => c.Kind == SnapshotNodeKind.Snapshot && !c.IsRetained && c.Title == name))
                    return node.Kind == SnapshotNodeKind.Snapshot ? node.Title : string.Empty;
            }
            return string.Empty;
        }

        /// <summary>Where "Vous êtes ici" hangs, "" for the original state.</summary>
        public static string CurrentParent(SnapshotTreeNode root)
        {
            foreach (var node in root.SelfAndDescendants())
            {
                if (node.Children.Any(c => c.Kind == SnapshotNodeKind.Current))
                    return node.Kind == SnapshotNodeKind.Snapshot ? node.Title : string.Empty;
            }
            return string.Empty;
        }
    }
}
