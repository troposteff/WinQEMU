using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Qemu
{
    /// <summary>The disks of an encrypted machine (see <see cref="DiskKey"/>).</summary>
    public sealed partial class VmSession
    {
        /// <summary>
        /// The password of the machine's disks, given by the shell before <see cref="StartAsync"/>. Kept for the
        /// session's life: QEMU is started again at every guest reboot, and a live snapshot creates encrypted overlays.
        /// </summary>
        public DiskKey? DiskKey { get; set; }

        /// <summary>
        /// The secret object and the key options of each disk, with the pipe that hands QEMU the password; null for a
        /// machine without an encrypted image.
        /// </summary>
        private async Task<(DiskKeyArguments Arguments, SecretPipe Pipe)?> PrepareDiskKeyAsync(CancellationToken cancellationToken)
        {
            if (DiskKey is null)
                return null;

            // Read from the images rather than from the descriptor: a linked clone's chain goes on below its base,
            // which only the other machine's descriptor describes. No password needed for that.
            var images = new QemuImgService(_installation);
            var levels = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal);
            foreach (var disk in Config.Disks.Where(d => d.Format == DiskFormat.Qcow2).ToList())
            {
                var path = Config.ResolvePath(disk.FileName);
                if (!File.Exists(path))
                    continue;
                var chain = await images.GetEncryptedLevelsAsync(path, cancellationToken).ConfigureAwait(false);
                if (chain.Contains(true))
                    levels[disk.Id] = chain;
            }

            if (levels.Count == 0)
                return null;

            var pipe = SecretPipe.Serve(DiskKey.ToBytes());
            AppendLog($"Encrypted disks: {string.Join(", ", levels.Keys)}; the password is read from {pipe.Path}.");
            return (new DiskKeyArguments(pipe.Path, levels), pipe);
        }

        /// <summary>
        /// A live snapshot of an encrypted disk: the overlay is created encrypted by qemu-img (without opening the base,
        /// which QEMU holds), opened in QEMU with the password it already has (blockdev-add), then put on top of the
        /// disk (blockdev-snapshot). blockdev-snapshot-sync would create a clear overlay.
        /// </summary>
        /// <remarks>
        /// Verified on a running qemu-system-x86_64 11.1: the overlay keeps the base written by qemu-img, writes after
        /// the snapshot land in it encrypted, the base is left as it was, and the chain opens again with a key per level.
        /// </remarks>
        private async Task CreateEncryptedOverlayAsync(QmpClient qmp, DiskConfig disk, string overlayPath, CancellationToken cancellationToken)
        {
            if (DiskKey is null)
                throw new DiskPasswordException(EncryptionText.ImageLocked, isWrongPassword: false);

            var top = Config.ResolvePath(disk.FileName);
            var images = new QemuImgService(_installation).WithKey(DiskKey);
            var size = (await images.GetInfoAsync(top, cancellationToken).ConfigureAwait(false)).VirtualSizeBytes;
            // Absolute, as blockdev-snapshot-sync writes it: the same chain as the clear overlays.
            await images.CreateOverlayAsync(overlayPath, size, QemuArgumentBuilder.QemuReadablePath(top), cancellationToken).ConfigureAwait(false);

            var node = "wq-overlay-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var added = false;
            try
            {
                await qmp.ExecuteAsync(
                    "blockdev-add",
                    new JsonObject
                    {
                        ["driver"] = "qcow2",
                        ["node-name"] = node,
                        ["discard"] = disk.Discard ? "unmap" : "ignore",
                        ["file"] = new JsonObject { ["driver"] = "file", ["filename"] = QemuArgumentBuilder.QemuReadablePath(overlayPath) },
                        ["encrypt"] = new JsonObject { ["format"] = "luks", ["key-secret"] = QemuArgumentBuilder.DiskKeySecretId },
                        // blockdev-snapshot wants an overlay with no backing of its own: the disk's image becomes it.
                        ["backing"] = null
                    },
                    cancellationToken).ConfigureAwait(false);
                added = true;

                await qmp.ExecuteAsync("blockdev-snapshot", new JsonObject { ["node"] = disk.Id, ["overlay"] = node }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                if (added)
                {
                    try
                    {
                        await qmp.ExecuteAsync("blockdev-del", new JsonObject { ["node-name"] = node }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (QmpException)
                    {
                        // Left open in QEMU until it exits; the file goes below only if QEMU let it go.
                    }
                }
                try
                {
                    File.Delete(overlayPath);
                }
                catch (IOException)
                {
                }
                throw;
            }
        }
    }
}
