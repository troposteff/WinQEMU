using WinQEMU.Core.Models;

namespace WinQEMU.Core.Services
{
    /// <summary>One linked-clone disk and the image it reads from, as an absolute path.</summary>
    public sealed record LinkedCloneDependency(string BasePath, VmConfig CloneMachine, string DiskId);

    /// <summary>
    /// Keeps the images linked clones are built on from being changed. Every operation that could write to
    /// or delete such an image asks here first, and is refused with the reason when it would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A linked clone's disk is a qcow2 overlay holding only what the clone wrote; everything else is read
    /// from the original's image. Change one byte of that image and every clone built on it silently reads
    /// different data — a corrupted filesystem, with no error anywhere.
    /// </para>
    /// <para>
    /// The dependencies are derived from the clones' descriptors each time, never stored on the original:
    /// a deleted or detached clone then frees the image by itself, and no record can go stale. The cost is
    /// that a clone the library does not list (removed from it, or outside its folder) protects nothing.
    /// </para>
    /// <para>
    /// The rule on a chain, top first: removing the overlay at index <c>i</c> — restoring or deleting its
    /// live snapshot, reverting to the base, merging — deletes images <c>0..i</c> and writes into or
    /// resumes writing to image <c>i+1</c>. It is refused when any of images <c>0..i+1</c> is a clone's
    /// base. The images under a base are covered by the same bound, since reaching them means passing it.
    /// </para>
    /// </remarks>
    public static class LinkedCloneGuard
    {
        public static IReadOnlyList<LinkedCloneDependency> Dependencies(IEnumerable<VmConfig> machines) =>
            machines
                .SelectMany(vm => vm.Disks
                    .Where(d => d.IsLinkedClone)
                    .Select(d => new LinkedCloneDependency(Normalize(vm.ResolvePath(d.LinkedBaseFileName)), vm, d.Id)))
                .ToList();

        /// <summary>
        /// Why the overlay at <paramref name="overlayIndex"/> of the disk's chain (0 = the image the machine
        /// writes to) cannot be removed, or null when it can.
        /// </summary>
        public static string? WhyOverlayCannotBeRemoved(
            VmConfig machine, DiskConfig disk, int overlayIndex, IReadOnlyList<LinkedCloneDependency> dependencies)
        {
            var reached = disk.ChainFromTop
                .Take(overlayIndex + 2)
                .Select(file => Normalize(machine.ResolvePath(file)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var clones = ClonesOf(dependencies.Where(d => reached.Contains(d.BasePath) && d.CloneMachine.Id != machine.Id));
            return clones is null
                ? null
                : $"Le disque « {disk.Id} » sert de base à {clones} : cette opération modifierait l'image " +
                  "sur laquelle le clone repose et corromprait ses données. Supprimez le clone, ou rendez ses " +
                  "disques indépendants avec « Convertir… », avant de recommencer.";
        }

        /// <summary>Why restoring or deleting the live snapshot <paramref name="name"/> is refused, or null.</summary>
        public static string? WhyLiveSnapshotIsLocked(
            VmConfig machine, string name, IReadOnlyList<LinkedCloneDependency> dependencies)
        {
            foreach (var disk in machine.Disks)
            {
                var chain = disk.ChainFromTop;
                for (var index = 0; index < chain.Count - 1; index++)
                {
                    if (DiskConfig.SnapshotNameOf(disk.Id, chain[index]) != name)
                        continue;

                    if (WhyOverlayCannotBeRemoved(machine, disk, index, dependencies) is { } reason)
                        return reason;
                }
            }

            return null;
        }

        /// <summary>
        /// Why the machine's folder cannot be deleted, or null. A base outside the folder (an image attached
        /// from elsewhere) is not deleted with it, so it does not block.
        /// </summary>
        public static string? WhyFilesCannotBeDeleted(VmConfig machine, IReadOnlyList<LinkedCloneDependency> dependencies)
        {
            var folder = Normalize(machine.FolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var clones = ClonesOf(dependencies.Where(d =>
                d.CloneMachine.Id != machine.Id
                && d.BasePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));

            return clones is null
                ? null
                : $"Les fichiers de cette machine servent de base à {clones}. Les supprimer détruirait le " +
                  "clone. Supprimez d'abord le clone, ou rendez ses disques indépendants avec « Convertir… ».";
        }

        /// <summary>Why <paramref name="path"/> cannot be attached as a disk, or null.</summary>
        public static string? WhyImageCannotBeAttached(string path, IReadOnlyList<LinkedCloneDependency> dependencies)
        {
            var full = Normalize(path);
            var clones = ClonesOf(dependencies.Where(d => string.Equals(d.BasePath, full, StringComparison.OrdinalIgnoreCase)));
            return clones is null
                ? null
                : $"Cette image sert de base à {clones} : une machine qui écrirait dedans corromprait le clone.";
        }

        /// <summary>"le clone lié « A »" or "les clones liés « A », « B »"; null when there are none.</summary>
        private static string? ClonesOf(IEnumerable<LinkedCloneDependency> dependencies)
        {
            var names = dependencies
                .Select(d => d.CloneMachine.Name)
                .Distinct(StringComparer.CurrentCulture)
                .Select(n => $"« {n} »")
                .ToList();

            return names.Count switch
            {
                0 => null,
                1 => $"le clone lié {names[0]}",
                _ => $"les clones liés {string.Join(", ", names)}"
            };
        }

        private static string Normalize(string path) => Path.GetFullPath(path);
    }
}
