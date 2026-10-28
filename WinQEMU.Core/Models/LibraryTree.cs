using System.Globalization;

namespace WinQEMU.Core.Models
{
    /// <summary>How the machines of each folder are ordered in the library.</summary>
    public enum LibrarySort
    {
        Name,
        /// <summary>Running machines first: which ones are up is what the list is mostly looked at for.</summary>
        State,
        /// <summary>The most recently started first (<see cref="VmConfig.LastStartedUtc"/>); never started last.</summary>
        LastUsed
    }

    /// <summary>Why a folder operation was refused, for the shell to put into words.</summary>
    public enum FolderProblem
    {
        None,
        EmptyName,
        /// <summary>A sibling already has that name: two "Linux" side by side could not be told apart.</summary>
        NameTaken,
        NotFound,
        /// <summary>A folder moved into itself or one of its own subfolders would leave the tree.</summary>
        IntoItself
    }

    /// <summary>One folder of the library. Machines are not listed here: <see cref="LibraryTree.Machines"/> points at folders.</summary>
    public sealed class LibraryFolder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        /// <summary>The folder holding this one, or null at the root.</summary>
        public string? ParentId { get; set; }

        public bool IsExpanded { get; set; } = true;
    }

    /// <summary>
    /// The library's folders, as VMware Workstation's inventory has them: the application's view of its
    /// machines, not part of any machine. Kept in <see cref="Services.AppSettings"/> rather than in the
    /// descriptors, so filing a machine never rewrites its .wqvm, and a machine folder copied elsewhere does
    /// not drag a folder name from another library with it.
    /// </summary>
    /// <remarks>
    /// Stored flat (each folder names its parent) rather than nested: a move is one field, and a file edited
    /// by hand cannot hide a folder inside two parents. <see cref="Repair"/> undoes what a hand edit can still
    /// break (a missing parent, a loop).
    /// </remarks>
    public sealed class LibraryTree
    {
        private List<LibraryFolder> _folders = new();
        public List<LibraryFolder> Folders { get => _folders; set => _folders = value ?? new(); }

        private Dictionary<Guid, string> _machines = new();
        /// <summary>The folder of each machine filed in one, by <see cref="VmConfig.Id"/>. A machine missing here sits at the root.</summary>
        public Dictionary<Guid, string> Machines { get => _machines; set => _machines = value ?? new(); }

        /// <summary>Applies within every folder; folders themselves are always ordered by name.</summary>
        public LibrarySort Sort { get; set; } = LibrarySort.Name;

        public LibraryFolder? Find(string? id) =>
            id is null ? null : Folders.FirstOrDefault(f => f.Id == id);

        public IEnumerable<LibraryFolder> ChildrenOf(string? parentId) =>
            Folders.Where(f => f.ParentId == parentId);

        /// <summary>The folder a machine is in, or null for the root (a folder since deleted counts as the root).</summary>
        public string? FolderOf(Guid machine) =>
            Machines.TryGetValue(machine, out var id) && Find(id) is not null ? id : null;

        /// <summary>From the root down to <paramref name="id"/> included; empty for the root.</summary>
        public IReadOnlyList<LibraryFolder> PathOf(string? id)
        {
            var path = new List<LibraryFolder>();
            // Bounded by the number of folders, so a loop in a hand-edited file cannot hang the caller.
            for (var folder = Find(id); folder is not null && path.Count <= Folders.Count; folder = Find(folder.ParentId))
                path.Insert(0, folder);
            return path;
        }

        /// <summary>Whether <paramref name="id"/> is <paramref name="ancestorId"/> or lies somewhere inside it.</summary>
        public bool IsSameOrInside(string? id, string ancestorId) =>
            id == ancestorId || PathOf(id).Any(f => f.Id == ancestorId);

        /// <summary>Every folder, depth first, each level by name, with its depth (0 at the root).</summary>
        public IReadOnlyList<(LibraryFolder Folder, int Depth)> AllFolders()
        {
            var all = new List<(LibraryFolder, int)>();
            void Walk(string? parentId, int depth)
            {
                foreach (var folder in ChildrenOf(parentId).OrderBy(f => f.Name, NameComparer))
                {
                    all.Add((folder, depth));
                    Walk(folder.Id, depth + 1);
                }
            }
            Walk(null, 0);
            return all;
        }

        /// <summary>
        /// Folder names compare as Windows compares file names: "linux" and "Linux" side by side would read
        /// as the same folder. Read each time rather than kept: it follows the culture of the moment.
        /// </summary>
        public static StringComparer NameComparer => StringComparer.CurrentCultureIgnoreCase;

        public FolderProblem CheckName(string? name, string? parentId, string? exceptId = null)
        {
            var trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                return FolderProblem.EmptyName;
            if (parentId is not null && Find(parentId) is null)
                return FolderProblem.NotFound;
            return ChildrenOf(parentId).Any(f => f.Id != exceptId && NameComparer.Equals(f.Name, trimmed))
                ? FolderProblem.NameTaken
                : FolderProblem.None;
        }

        /// <summary><paramref name="name"/>, or "name (2)", "name (3)"… whichever is free under <paramref name="parentId"/>.</summary>
        public string UniqueName(string name, string? parentId, string? exceptId = null)
        {
            var trimmed = name.Trim();
            var candidate = trimmed;
            for (var n = 2; ChildrenOf(parentId).Any(f => f.Id != exceptId && NameComparer.Equals(f.Name, candidate)); n++)
                candidate = $"{trimmed} ({n.ToString(CultureInfo.InvariantCulture)})";
            return candidate;
        }

        public FolderProblem CreateFolder(string name, string? parentId, out LibraryFolder? created)
        {
            created = null;
            var problem = CheckName(name, parentId);
            if (problem != FolderProblem.None)
                return problem;
            created = new LibraryFolder { Name = name.Trim(), ParentId = parentId };
            Folders.Add(created);
            // Opening the parent shows where the new folder went.
            Reveal(parentId);
            return FolderProblem.None;
        }

        public FolderProblem Rename(string id, string name)
        {
            if (Find(id) is not { } folder)
                return FolderProblem.NotFound;
            var problem = CheckName(name, folder.ParentId, exceptId: id);
            if (problem == FolderProblem.None)
                folder.Name = name.Trim();
            return problem;
        }

        /// <summary>Why <paramref name="id"/> cannot go into <paramref name="newParentId"/> (null: the root), or None.</summary>
        public FolderProblem WhyFolderCannotMove(string id, string? newParentId)
        {
            if (Find(id) is not { } folder || (newParentId is not null && Find(newParentId) is null))
                return FolderProblem.NotFound;
            if (newParentId is not null && IsSameOrInside(newParentId, id))
                return FolderProblem.IntoItself;
            if (folder.ParentId == newParentId)
                return FolderProblem.None;
            return CheckName(folder.Name, newParentId, exceptId: id);
        }

        public FolderProblem MoveFolder(string id, string? newParentId)
        {
            var problem = WhyFolderCannotMove(id, newParentId);
            if (problem == FolderProblem.None)
                Find(id)!.ParentId = newParentId;
            return problem;
        }

        /// <summary>Files a machine in <paramref name="folderId"/>, or at the root for null.</summary>
        public FolderProblem MoveMachine(Guid machine, string? folderId)
        {
            if (folderId is null)
            {
                Machines.Remove(machine);
                return FolderProblem.None;
            }
            if (Find(folderId) is null)
                return FolderProblem.NotFound;
            Machines[machine] = folderId;
            return FolderProblem.None;
        }

        /// <summary>
        /// Removes a folder, never a machine: what it held moves up to its parent. A subfolder whose name is
        /// already taken there gets a number ("Linux (2)") rather than refusing the whole deletion.
        /// </summary>
        public bool DeleteFolder(string id)
        {
            if (Find(id) is not { } folder)
                return false;

            Folders.Remove(folder);
            foreach (var child in Folders.Where(f => f.ParentId == id).ToList())
            {
                child.ParentId = folder.ParentId;
                child.Name = UniqueName(child.Name, folder.ParentId, exceptId: child.Id);
            }
            foreach (var machine in Machines.Where(m => m.Value == id).Select(m => m.Key).ToList())
            {
                if (folder.ParentId is null)
                    Machines.Remove(machine);
                else
                    Machines[machine] = folder.ParentId;
            }
            return true;
        }

        /// <summary>Expands every folder down to <paramref name="folderId"/> included. True when one was collapsed.</summary>
        public bool Reveal(string? folderId)
        {
            var changed = false;
            foreach (var folder in PathOf(folderId).Where(f => !f.IsExpanded))
            {
                folder.IsExpanded = true;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Puts the tree back in order after loading: what a hand edit or an older file can leave behind.
        /// Folders with a missing parent go to the root, a loop of parents is cut, a duplicate id is dropped,
        /// a blank or duplicate name is replaced, and machines pointing at no folder go back to the root. With
        /// <paramref name="library"/>, machines no longer in the library are forgotten. True when anything changed.
        /// </summary>
        public bool Repair(IEnumerable<Guid>? library, string unnamedFolder)
        {
            var changed = false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in Folders.ToList())
            {
                if (string.IsNullOrWhiteSpace(folder.Id) || !seen.Add(folder.Id))
                {
                    Folders.Remove(folder);
                    changed = true;
                }
            }

            foreach (var folder in Folders)
            {
                if (folder.ParentId is not null && (folder.ParentId == folder.Id || Find(folder.ParentId) is null))
                {
                    folder.ParentId = null;
                    changed = true;
                }
            }

            // A loop (A in B, B in A) never reaches the root: cut it at the folder where the walk came back.
            foreach (var folder in Folders)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { folder.Id };
                for (var parent = Find(folder.ParentId); parent is not null; parent = Find(parent.ParentId))
                {
                    if (!visited.Add(parent.Id))
                    {
                        parent.ParentId = null;
                        changed = true;
                        break;
                    }
                }
            }

            foreach (var folder in Folders)
            {
                var name = string.IsNullOrWhiteSpace(folder.Name) ? unnamedFolder : folder.Name.Trim();
                // Only the folders before this one count, so the first of two "Linux" keeps its name.
                var before = Folders.TakeWhile(f => !ReferenceEquals(f, folder))
                    .Where(f => f.ParentId == folder.ParentId)
                    .Select(f => f.Name)
                    .ToList();
                var unique = name;
                for (var n = 2; before.Contains(unique, NameComparer); n++)
                    unique = $"{name} ({n.ToString(CultureInfo.InvariantCulture)})";
                if (unique != folder.Name)
                {
                    folder.Name = unique;
                    changed = true;
                }
            }

            var known = library?.ToHashSet();
            foreach (var (machine, folderId) in Machines.ToList())
            {
                if (Find(folderId) is null || (known is not null && !known.Contains(machine)))
                {
                    Machines.Remove(machine);
                    changed = true;
                }
            }

            return changed;
        }
    }

    /// <summary>What the library orders a machine by.</summary>
    public readonly record struct MachineSortKey(string Name, VmState State, DateTime? LastStartedUtc)
    {
        public static MachineSortKey Of(VmConfig config, VmState state) => new(config.Name, state, config.LastStartedUtc);
    }

    /// <summary>One line of the library as shown: a folder, or a machine, at its depth in the tree.</summary>
    public sealed record LibraryEntry<T>(LibraryFolder? Folder, T? Machine, int Depth) where T : class;

    /// <summary>Turns the tree into the lines of the library list, and counts what each folder holds.</summary>
    public static class LibraryLayout
    {
        /// <summary>
        /// Folders first then machines, at every level; folders by name, machines by <paramref name="sort"/>.
        /// The content of a collapsed folder is left out. Machines in no known folder are at the root.
        /// </summary>
        public static IReadOnlyList<LibraryEntry<T>> Flatten<T>(
            LibraryTree tree, IEnumerable<T> machines, Func<T, Guid> id, Func<T, MachineSortKey> key, LibrarySort sort)
            where T : class
        {
            var byFolder = machines
                .GroupBy(m => tree.FolderOf(id(m)) ?? string.Empty)
                .ToDictionary(g => g.Key, g => Order(g, key, sort));

            var lines = new List<LibraryEntry<T>>();
            void Walk(string? parentId, int depth)
            {
                foreach (var folder in tree.ChildrenOf(parentId).OrderBy(f => f.Name, LibraryTree.NameComparer))
                {
                    lines.Add(new LibraryEntry<T>(folder, null, depth));
                    if (folder.IsExpanded)
                        Walk(folder.Id, depth + 1);
                }
                if (byFolder.TryGetValue(parentId ?? string.Empty, out var inside))
                    lines.AddRange(inside.Select(m => new LibraryEntry<T>(null, m, depth)));
            }
            // A guard against a loop the tree was not repaired of: such folders never hang from the root, so
            // the walk simply does not reach them.
            Walk(null, 0);
            return lines;
        }

        /// <summary>Machines ordered by <paramref name="sort"/>, ties by name, then by the order given.</summary>
        public static IReadOnlyList<T> Order<T>(IEnumerable<T> machines, Func<T, MachineSortKey> key, LibrarySort sort) =>
            machines.Select((m, index) => (m, k: key(m), index))
                .OrderBy(x => x.k, new MachineComparer(sort))
                .ThenBy(x => x.index)
                .Select(x => x.m)
                .ToList();

        /// <summary>Whether a QEMU process is up for the machine: what a folder's "running" count counts.</summary>
        public static bool IsAlive(VmState state) =>
            state is VmState.Starting or VmState.Running or VmState.Paused or VmState.Stopping;

        /// <summary>For each folder, the machines it holds, subfolders included, and how many of them are alive.</summary>
        public static IReadOnlyDictionary<string, (int Machines, int Running)> Count<T>(
            LibraryTree tree, IEnumerable<T> machines, Func<T, Guid> id, Func<T, VmState> state)
        {
            var counts = tree.Folders.ToDictionary(f => f.Id, _ => (Machines: 0, Running: 0));
            foreach (var machine in machines)
            {
                var alive = IsAlive(state(machine));
                foreach (var folder in tree.PathOf(tree.FolderOf(id(machine))))
                {
                    var (total, running) = counts[folder.Id];
                    counts[folder.Id] = (total + 1, running + (alive ? 1 : 0));
                }
            }
            return counts;
        }

        /// <summary>
        /// Makes <paramref name="rows"/> equal to <paramref name="target"/> without ever removing <paramref name="keep"/>
        /// while it stays in the list: lines around it are removed and inserted, it is not. For the shell's list, whose
        /// ListView drops its selection with the selected line, and a dropped selection detaches the console. Not a
        /// move either: an ObservableCollection's move reaches the ListView as a removal and an insertion.
        /// </summary>
        public static void Synchronize<T>(IList<T> rows, IReadOnlyList<T> target, T? keep) where T : class
        {
            var wanted = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(rows[i]))
                    rows.RemoveAt(i);
            }

            for (var i = 0; i < target.Count; i++)
            {
                var row = target[i];
                if (i < rows.Count && ReferenceEquals(rows[i], row))
                    continue;

                var at = -1;
                for (var j = i + 1; j < rows.Count; j++)
                {
                    if (ReferenceEquals(rows[j], row))
                    {
                        at = j;
                        break;
                    }
                }

                if (at < 0)
                    rows.Insert(i, row);
                else if (!ReferenceEquals(row, keep))
                {
                    rows.RemoveAt(at);
                    rows.Insert(i, row);
                }
                else
                {
                    // The kept line is further down: what stands before it goes, and comes back at its own place.
                    for (var j = at - 1; j >= i; j--)
                        rows.RemoveAt(j);
                }
            }

            while (rows.Count > target.Count)
                rows.RemoveAt(rows.Count - 1);
        }

        private sealed class MachineComparer : IComparer<MachineSortKey>
        {
            private readonly LibrarySort _sort;

            public MachineComparer(LibrarySort sort) => _sort = sort;

            public int Compare(MachineSortKey a, MachineSortKey b)
            {
                var order = _sort switch
                {
                    LibrarySort.State => StateRank(a.State).CompareTo(StateRank(b.State)),
                    // Most recent first; a machine never started goes after all the others.
                    LibrarySort.LastUsed => (b.LastStartedUtc ?? DateTime.MinValue).CompareTo(a.LastStartedUtc ?? DateTime.MinValue),
                    _ => 0
                };
                return order != 0 ? order : LibraryTree.NameComparer.Compare(a.Name, b.Name);
            }

            // Up first, then what is on its way up or down, then failed, then off.
            private static int StateRank(VmState state) => state switch
            {
                VmState.Running => 0,
                VmState.Paused => 1,
                VmState.Starting => 2,
                VmState.Stopping => 3,
                VmState.Error => 4,
                _ => 5
            };
        }
    }
}
