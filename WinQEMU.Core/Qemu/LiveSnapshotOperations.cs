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
    /// </remarks>
    public sealed class LiveSnapshotOperations
    {
        private readonly QemuImgService _images;

        public LiveSnapshotOperations(QemuImgService images) => _images = images;

        /// <summary>
        /// Brings every disk back to the moment <paramref name="name"/> was taken. Everything written
        /// since — including later live snapshots — is discarded.
        /// </summary>
        public Task RevertAsync(VmConfig config, string name)
        {
            foreach (var disk in config.Disks)
            {
                var target = FindOverlay(disk, name);
                if (target is null)
                    continue;

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
            }

            return Task.CompletedTask;
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

                await _images.CommitAsync(config.ResolvePath(target), cancellationToken).ConfigureAwait(false);

                if (index > 0)
                {
                    var above = chain[index - 1];
                    // The original image keeps the disk's own format; every overlay is qcow2.
                    var belowFormat = index + 1 == chain.Count - 1 ? disk.Format : DiskFormat.Qcow2;
                    // Relative, as the overlays sit in the VM folder: the chain then survives moving it.
                    await _images.RebaseUnsafeAsync(
                        config.ResolvePath(above), Path.GetFileName(below), belowFormat, cancellationToken).ConfigureAwait(false);
                }

                disk.RemoveOverlay(target);
                var path = config.ResolvePath(target);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static string? FindOverlay(DiskConfig disk, string name)
        {
            var chain = disk.ChainFromTop;
            return chain.Take(chain.Count - 1).FirstOrDefault(file => DiskConfig.SnapshotNameOf(disk.Id, file) == name);
        }
    }
}
