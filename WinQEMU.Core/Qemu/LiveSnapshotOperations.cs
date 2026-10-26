using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// Restores or deletes a live disk snapshot, i.e. one level of each disk's overlay chain. The machine
    /// must be stopped: qemu-img refuses images a running QEMU holds open, and forcing it corrupts them.
    /// </summary>
    /// <remarks>
    /// The overlay named after a snapshot holds what was written <em>after</em> it was taken; the state at
    /// that moment is the image underneath. So restoring "Test 1" unstacks the "Test 1" overlay and every
    /// overlay above it, and deleting "Test 1" while keeping the data merges its overlay downwards.
    ///
    /// Only the descriptor's disks are updated here; saving the descriptor is the caller's job.
    ///
    /// No ConfigureAwait(false): the disks are data-bound, and moving along their chains raises
    /// PropertyChanged, which has to happen on the thread that called — the UI thread in the shell.
    /// </remarks>
    public sealed class LiveSnapshotOperations
    {
        private readonly QemuImgService _images;

        public LiveSnapshotOperations(QemuImgService images) => _images = images;

        /// <summary>
        /// Brings every disk back to the moment <paramref name="name"/> was taken. Everything written
        /// since — including later live snapshots — is discarded, except images a linked clone reads.
        /// </summary>
        /// <param name="isProtected">
        /// Whether a linked clone reads the image at this absolute path (<see cref="Services.LinkedCloneProtection"/>).
        /// Null when no clone exists.
        /// </param>
        /// <remarks>
        /// <para>
        /// Without a clone in the way, the overlays from the top down to the snapshot's are deleted and the
        /// disk writes to the image underneath again.
        /// </para>
        /// <para>
        /// When that image, or one of the overlays to delete, is read by a linked clone, it must not change:
        /// the disk then starts a fresh overlay on the snapshot's state instead of writing into it, as
        /// VMware does when reverting to a snapshot a linked clone was made from. Protected overlays leave
        /// the chain but stay on disk, recorded in <see cref="DiskConfig.RetainedImages"/> with what they are
        /// layered on, until no clone needs them. The fresh overlay keeps the snapshot's name when its old
        /// overlay was deleted, and takes "name (2)" when that file is kept for a clone — on every disk alike,
        /// so the levels still group into one snapshot.
        /// </para>
        /// </remarks>
        public async Task RevertAsync(
            VmConfig config,
            string name,
            Func<string, bool>? isProtected = null,
            CancellationToken cancellationToken = default)
        {
            var fresh = new List<(DiskConfig Disk, string Temporary)>();
            foreach (var disk in config.Disks)
            {
                var chain = disk.ChainFromTop.ToList();
                var index = OverlayIndex(disk, name);
                if (index < 0)
                    continue;

                if (await RevertDiskAsync(config, disk, chain, index, isProtected, cancellationToken) is { } temporary)
                    fresh.Add((disk, temporary));
            }

            NameFreshOverlays(config, fresh, name);
        }

        /// <summary>
        /// "Revenir à l'image de base" for one disk: <see cref="RevertAsync"/> on its top overlay alone.
        /// </summary>
        public async Task RevertTopAsync(
            VmConfig config,
            DiskConfig disk,
            Func<string, bool>? isProtected = null,
            CancellationToken cancellationToken = default)
        {
            if (!disk.IsOverlay)
                throw new InvalidOperationException($"Disk '{disk.Id}' is not an overlay.");

            var chain = disk.ChainFromTop.ToList();
            var name = DiskConfig.SnapshotNameOf(disk.Id, chain[0]);
            if (await RevertDiskAsync(config, disk, chain, 0, isProtected, cancellationToken) is { } temporary)
                NameFreshOverlays(config, new[] { (disk, temporary) }, name);
        }

        /// <summary>
        /// Takes the disk back to the state under <c>chain[index]</c>. Returns the temporary name of the
        /// fresh overlay when one had to be started, for <see cref="NameFreshOverlays"/> to settle.
        /// </summary>
        private async Task<string?> RevertDiskAsync(
            VmConfig config,
            DiskConfig disk,
            IReadOnlyList<string> chain,
            int index,
            Func<string, bool>? isProtected,
            CancellationToken cancellationToken)
        {
            var target = chain[index];
            var firstProtected = isProtected is null
                ? -1
                : chain.Select((file, position) => (file, position))
                    .Where(entry => isProtected(config.ResolvePath(entry.file)))
                    .Select(entry => entry.position)
                    .DefaultIfEmpty(-1)
                    .Min();

            if (firstProtected < 0 || firstProtected > index + 1)
            {
                while (disk.IsOverlay)
                {
                    // Repoint first, delete second, as for a single overlay: if a delete fails the
                    // descriptor already names an image that still exists.
                    var removed = disk.PopOverlay();
                    var path = config.ResolvePath(removed);
                    if (File.Exists(path))
                        File.Delete(path);

                    if (removed == target)
                        break;
                }

                return null;
            }

            // The state to return to is read by a clone, or sits under an image that is: start a fresh
            // overlay on it. Created before the descriptor changes, so a failure leaves everything as it was.
            var state = chain[index + 1];
            var statePath = config.ResolvePath(state);
            var size = (await _images.GetInfoAsync(statePath, cancellationToken)).VirtualSizeBytes;
            var temporary = FreeName(config, $"{disk.Id}-{DiskConfig.SnapshotNameOf(disk.Id, target)}.restauration");
            await _images.CreateAsync(
                config.ResolvePath(temporary), DiskFormat.Qcow2, size,
                // Relative, as the other overlays: the chain then survives moving the folder.
                Path.GetRelativePath(config.FolderPath, statePath),
                cancellationToken);

            while (disk.PopOverlay() != target)
            {
            }
            disk.PushOverlay(temporary);

            // Above every protected image: what the machine wrote since, thrown away as in a plain restore.
            // From the first protected one down: kept for the clones, with what each is layered on.
            for (var level = 0; level <= index; level++)
            {
                if (level < firstProtected)
                {
                    var path = config.ResolvePath(chain[level]);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                {
                    disk.RetainedImages.Add(new RetainedImage { FileName = chain[level], BackingFileName = chain[level + 1] });
                }
            }

            return temporary;
        }

        /// <summary>
        /// Gives the fresh overlays their final name, the same on every disk: "disk0-Test.qcow2" when free,
        /// otherwise "disk0-Test (2).qcow2" and so on.
        /// </summary>
        private static void NameFreshOverlays(VmConfig config, IReadOnlyList<(DiskConfig Disk, string Temporary)> fresh, string name)
        {
            if (fresh.Count == 0)
                return;

            string Final(DiskConfig disk, int attempt) =>
                attempt == 1 ? $"{disk.Id}-{name}.qcow2" : $"{disk.Id}-{name} ({attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}).qcow2";

            var attempt = 1;
            while (fresh.Any(f => File.Exists(config.ResolvePath(Final(f.Disk, attempt)))))
                attempt++;

            foreach (var (disk, temporary) in fresh)
            {
                // Nothing reads the fresh overlay yet but the descriptor, so renaming it is safe.
                var final = Final(disk, attempt);
                File.Move(config.ResolvePath(temporary), config.ResolvePath(final));
                disk.PopOverlay();
                disk.PushOverlay(final);
            }
        }

        private static string FreeName(VmConfig config, string stem)
        {
            var candidate = stem + ".qcow2";
            for (var attempt = 2; File.Exists(config.ResolvePath(candidate)); attempt++)
                candidate = $"{stem} {attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}.qcow2";
            return candidate;
        }

        /// <summary>
        /// Deletes the snapshot <paramref name="name"/> but keeps everything the disks hold now: its
        /// overlay is committed into the image below, and an overlay above it, if any, is repointed at
        /// that image.
        /// </summary>
        /// <remarks>
        /// The repoint is an unsafe rebase (metadata only), which is correct precisely because the commit
        /// has just made the image below hold what the removed overlay presented. Verified on a three-level
        /// chain with a distinct pattern written at each level: all three remain readable from the top and
        /// qemu-img check finds no error.
        /// </remarks>
        public async Task MergeAsync(VmConfig config, string name, CancellationToken cancellationToken = default)
        {
            foreach (var disk in config.Disks)
            {
                var target = FindOverlay(disk, name);
                if (target is null)
                    continue;

                var chain = disk.ChainFromTop;
                var index = chain.ToList().IndexOf(target);
                var below = chain[index + 1];

                await _images.CommitAsync(config.ResolvePath(target), cancellationToken);

                if (index > 0)
                {
                    var above = chain[index - 1];
                    // The original image keeps the disk's own format; every overlay is qcow2.
                    var belowFormat = index + 1 == chain.Count - 1 ? disk.Format : DiskFormat.Qcow2;
                    // Relative, as the overlays sit in the VM folder: the chain then survives moving it.
                    await _images.RebaseUnsafeAsync(
                        config.ResolvePath(above), Path.GetFileName(below), belowFormat, cancellationToken);
                }

                disk.RemoveOverlay(target);
                var path = config.ResolvePath(target);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static string? FindOverlay(DiskConfig disk, string name)
        {
            var index = OverlayIndex(disk, name);
            return index < 0 ? null : disk.ChainFromTop[index];
        }

        private static int OverlayIndex(DiskConfig disk, string name)
        {
            var chain = disk.ChainFromTop;
            for (var index = 0; index < chain.Count - 1; index++)
            {
                if (DiskConfig.SnapshotNameOf(disk.Id, chain[index]) == name)
                    return index;
            }

            return -1;
        }
    }
}
