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
            // Everything that can fail for any disk (reading a size, creating a fresh overlay: a full volume, a key
            // that does not open) is done for all of them before anything is deleted. Disk by disk, a failure on the
            // second left the first one restored, its newer data gone, and the second at the present state.
            var plans = new List<RevertPlan>();
            try
            {
                foreach (var disk in config.Disks)
                {
                    var index = OverlayIndex(disk, name);
                    if (index >= 0)
                        plans.Add(await PrepareRevertAsync(config, disk, index, isProtected, cancellationToken));
                }
            }
            catch
            {
                foreach (var temporary in plans.Select(p => p.Temporary).OfType<string>())
                    TryDelete(config.ResolvePath(temporary));
                throw;
            }

            var fresh = new List<(DiskConfig Disk, string Temporary)>();
            foreach (var plan in plans)
            {
                ApplyRevert(config, plan);
                if (plan.Temporary is { } temporary)
                    fresh.Add((plan.Disk, temporary));
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
            var plan = await PrepareRevertAsync(config, disk, 0, isProtected, cancellationToken);
            ApplyRevert(config, plan);
            if (plan.Temporary is { } temporary)
                NameFreshOverlays(config, new[] { (disk, temporary) }, name);
        }

        /// <summary>One disk's way back to the state under <c>Chain[Index]</c>; <see cref="Temporary"/>, a fresh overlay already created.</summary>
        private sealed record RevertPlan(DiskConfig Disk, IReadOnlyList<string> Chain, int Index, int FirstProtected, string? Temporary);

        /// <summary>
        /// What can fail, done before anything changes: when the state to return to is read by a clone, or sits under
        /// an image that is, the fresh overlay the disk will start from is created on it now.
        /// </summary>
        private async Task<RevertPlan> PrepareRevertAsync(
            VmConfig config,
            DiskConfig disk,
            int index,
            Func<string, bool>? isProtected,
            CancellationToken cancellationToken)
        {
            var chain = disk.ChainFromTop.ToList();
            var target = chain[index];
            var firstProtected = isProtected is null
                ? -1
                : chain.Select((file, position) => (file, position))
                    .Where(entry => isProtected(config.ResolvePath(entry.file)))
                    .Select(entry => entry.position)
                    .DefaultIfEmpty(-1)
                    .Min();

            if (firstProtected < 0 || firstProtected > index + 1)
                return new RevertPlan(disk, chain, index, firstProtected, null);

            var state = chain[index + 1];
            var statePath = config.ResolvePath(state);
            var size = (await _images.GetInfoAsync(statePath, cancellationToken)).VirtualSizeBytes;
            var temporary = FreeName(config, $"{disk.Id}-{DiskConfig.SnapshotNameOf(disk.Id, target)}.restauration");
            await _images.CreateAsync(
                config.ResolvePath(temporary), DiskFormat.Qcow2, size,
                // Relative, as the other overlays: the chain then survives moving the folder.
                Path.GetRelativePath(config.FolderPath, statePath),
                cancellationToken);
            return new RevertPlan(disk, chain, index, firstProtected, temporary);
        }

        /// <summary>
        /// Takes the disk back: the descriptor changes first (and records what is kept for the clones), then the files
        /// it no longer names are deleted. A delete that fails leaves a stray file, never a descriptor naming a
        /// missing one, nor an image a clone reads left out of both the chain and <see cref="DiskConfig.RetainedImages"/>
        /// (it was then no longer protected, and a later merge wrote into what the clone reads).
        /// </summary>
        private static void ApplyRevert(VmConfig config, RevertPlan plan)
        {
            var (disk, chain, index, firstProtected, temporary) = (plan.Disk, plan.Chain, plan.Index, plan.FirstProtected, plan.Temporary);
            var target = chain[index];
            var doomed = new List<string>();

            if (temporary is null)
            {
                while (disk.IsOverlay)
                {
                    var removed = disk.PopOverlay();
                    doomed.Add(removed);
                    if (removed == target)
                        break;
                }
            }
            else
            {
                while (disk.PopOverlay() != target)
                {
                }
                disk.PushOverlay(temporary);

                // Above every protected image: what the machine wrote since, thrown away as in a plain restore.
                // From the first protected one down: kept for the clones, with what each is layered on.
                for (var level = 0; level <= index; level++)
                {
                    if (level < firstProtected)
                        doomed.Add(chain[level]);
                    else
                        disk.RetainedImages.Add(new RetainedImage { FileName = chain[level], BackingFileName = chain[level + 1] });
                }
            }

            foreach (var file in doomed)
            {
                var path = config.ResolvePath(file);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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
                    // Relative to the overlay's own folder, as QEMU resolves it: the chain then survives moving the
                    // folder. Not the bare file name: the original image may be attached from elsewhere
                    // (D:\img\deb.qcow2), and the overlay then named "deb.qcow2" in the VM folder, which does not exist
                    // there (or is another image). On another drive GetRelativePath gives the absolute path.
                    var abovePath = config.ResolvePath(above);
                    await _images.RebaseUnsafeAsync(
                        abovePath, Path.GetRelativePath(Path.GetDirectoryName(abovePath)!, config.ResolvePath(below)),
                        belowFormat, cancellationToken);
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
