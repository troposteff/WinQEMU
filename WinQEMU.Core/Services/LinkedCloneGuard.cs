using WinQEMU.Core.Models;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Every image some linked clone reads, directly or through the images it is layered on, with the
    /// names of those clones. Built from the library's descriptors each time it is needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A linked clone's disk is a qcow2 overlay holding only what the clone wrote; everything else is read
    /// from the original's image, and from whatever that image is layered on. Change one byte anywhere down
    /// that line and the clone silently reads different data — a corrupted filesystem, with no error.
    /// </para>
    /// <para>
    /// The line is followed through two records: the chains of every disk, and the images a machine kept
    /// for its clones after leaving them (<see cref="DiskConfig.RetainedImages"/>). Nothing is stored on the
    /// original about its clones, so a deleted or detached clone frees its images by itself and no record
    /// can go stale. The cost is that a clone the library does not list (removed from it, or outside its
    /// folder) protects nothing.
    /// </para>
    /// </remarks>
    public sealed class LinkedCloneProtection
    {
        private readonly Dictionary<string, SortedSet<string>> _readers;

        private LinkedCloneProtection(Dictionary<string, SortedSet<string>> readers) => _readers = readers;

        public static LinkedCloneProtection Of(IEnumerable<VmConfig> machines)
        {
            var list = machines.ToList();
            var below = BackingMap(list);

            var readers = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var clone in list)
            {
                foreach (var disk in clone.Disks.Where(d => d.IsLinkedClone))
                {
                    foreach (var path in LineFrom(clone.ResolvePath(disk.LinkedBaseFileName), below))
                    {
                        if (!readers.TryGetValue(path, out var names))
                            readers[path] = names = new SortedSet<string>(StringComparer.CurrentCulture);
                        names.Add(clone.Name);
                    }
                }
            }

            return new LinkedCloneProtection(readers);
        }

        /// <summary>
        /// The image and everything it is layered on, top first, as far as the library knows. The visited
        /// check only guards against a cycle in hand-edited descriptors.
        /// </summary>
        public static IEnumerable<string> LineFrom(string path, IReadOnlyDictionary<string, string> below)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string? current = Normalize(path);
                 current is not null && visited.Add(current);
                 current = below.GetValueOrDefault(current))
            {
                yield return current;
            }
        }

        /// <summary>
        /// Every "this image is layered on that one" the library knows: consecutive images of each chain,
        /// each retained image's recorded base, and each linked disk's bottom on its base.
        /// </summary>
        public static Dictionary<string, string> BackingMap(IEnumerable<VmConfig> machines)
        {
            var below = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var machine in machines)
            {
                foreach (var disk in machine.Disks)
                {
                    var chain = disk.ChainFromTop.Select(f => Normalize(machine.ResolvePath(f))).ToList();
                    for (var index = 0; index < chain.Count - 1; index++)
                        below[chain[index]] = chain[index + 1];

                    if (disk.IsLinkedClone)
                        below[chain[^1]] = Normalize(machine.ResolvePath(disk.LinkedBaseFileName));

                    foreach (var retained in disk.RetainedImages)
                        below[Normalize(machine.ResolvePath(retained.FileName))] = Normalize(machine.ResolvePath(retained.BackingFileName));
                }
            }

            return below;
        }

        public bool IsProtected(string path) => _readers.ContainsKey(Normalize(path));

        /// <summary>
        /// "le clone lié « A »" or "les clones liés « A », « B »" for the clones reading any of the paths;
        /// null when none does.
        /// </summary>
        public string? DescribeReaders(IEnumerable<string> paths)
        {
            var names = paths
                .Select(Normalize)
                .Where(_readers.ContainsKey)
                .SelectMany(p => _readers[p])
                .Distinct(StringComparer.CurrentCulture)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .Select(n => $"« {n} »")
                .ToList();

            return names.Count switch
            {
                0 => null,
                1 => $"le clone lié {names[0]}",
                _ => $"les clones liés {string.Join(", ", names)}"
            };
        }

        public IEnumerable<string> ProtectedPaths => _readers.Keys;

        public static string Normalize(string path) => Path.GetFullPath(path);
    }

    /// <summary>
    /// The operations that would change an image a linked clone reads, and why each is refused.
    /// </summary>
    /// <remarks>
    /// The rule on a chain, top first: merging the overlay at index <c>i</c> into the image below — deleting
    /// its live snapshot, or "Fusionner" — deletes image <c>i</c> and writes into image <c>i+1</c>. It is
    /// refused when either is protected; everything under a protected image is protected too. Restoring is
    /// never refused: it leaves protected images in place (<see cref="Qemu.LiveSnapshotOperations"/>).
    /// </remarks>
    public static class LinkedCloneGuard
    {
        /// <summary>
        /// Why the overlay at <paramref name="overlayIndex"/> of the disk's chain (0 = the image the machine
        /// writes to) cannot be merged into the image below it, or null when it can.
        /// </summary>
        public static string? WhyOverlayCannotBeMerged(
            VmConfig machine, DiskConfig disk, int overlayIndex, LinkedCloneProtection protection)
        {
            var touched = disk.ChainFromTop.Skip(overlayIndex).Take(2).Select(machine.ResolvePath);
            var clones = protection.DescribeReaders(touched);
            return clones is null
                ? null
                : $"Le disque « {disk.Id} » sert de base à {clones} : supprimer ou fusionner ce niveau " +
                  "écrirait dans une image que le clone lit, et corromprait ses données. Restaurer reste " +
                  "possible : les images dont le clone a besoin sont alors conservées.";
        }

        /// <summary>Why the live snapshot <paramref name="name"/> cannot be deleted, or null when it can.</summary>
        public static string? WhyLiveSnapshotCannotBeDeleted(VmConfig machine, string name, LinkedCloneProtection protection)
        {
            foreach (var disk in machine.Disks)
            {
                var chain = disk.ChainFromTop;
                for (var index = 0; index < chain.Count - 1; index++)
                {
                    if (DiskConfig.SnapshotNameOf(disk.Id, chain[index]) == name
                        && WhyOverlayCannotBeMerged(machine, disk, index, protection) is { } reason)
                        return reason;
                }
            }

            return null;
        }

        /// <summary>
        /// Why the machine's folder cannot be deleted, or null. A protected image outside the folder (one
        /// attached from elsewhere) is not deleted with it, so it does not block.
        /// </summary>
        public static string? WhyFilesCannotBeDeleted(VmConfig machine, LinkedCloneProtection protection)
        {
            var folder = LinkedCloneProtection.Normalize(machine.FolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var clones = protection.DescribeReaders(
                protection.ProtectedPaths.Where(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));

            return clones is null
                ? null
                : $"Les fichiers de cette machine servent de base à {clones}. Les supprimer détruirait le " +
                  "clone. Supprimez d'abord le clone, ou rendez ses disques indépendants avec « Convertir… ».";
        }

        /// <summary>Why <paramref name="path"/> cannot be attached as a disk, or null.</summary>
        public static string? WhyImageCannotBeAttached(string path, LinkedCloneProtection protection)
        {
            var clones = protection.DescribeReaders(new[] { path });
            return clones is null
                ? null
                : $"Cette image sert de base à {clones} : une machine qui écrirait dedans corromprait le clone.";
        }
    }
}
