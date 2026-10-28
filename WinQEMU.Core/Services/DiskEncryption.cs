using System.Globalization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>What encrypting or decrypting left behind: images outside the machine's folder, not WinQEMU's to delete.</summary>
    public sealed record DiskEncryptionResult(IReadOnlyList<string> ImagesLeftInPlace);

    /// <summary>
    /// Encrypting, decrypting and changing the password of a stopped machine's disks, as VMware's VM encryption does for
    /// its disks (Qemu.DiskKey). The descriptor and the UEFI variable store stay in clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Encrypting and decrypting rewrite each disk with qemu-img convert, as « Convertir… » does: a new image holding
    /// the disk's current state, then the swap. LUKS cannot be added to or taken off an existing qcow2 in place. So the
    /// snapshots go (convert copies neither internal snapshots nor the overlays of live ones), it needs room for a copy
    /// of each disk, and the old images are deleted — not wiped: what they held may stay readable on the drive.
    /// </para>
    /// <para>
    /// Linked clones are refused either way: a clone reads its original's images, which must not change, and both
    /// sides would need the same password for good.
    /// </para>
    /// <para>
    /// No ConfigureAwait(false), as LiveSnapshotOperations: the disks are data-bound and changing them raises
    /// PropertyChanged, which belongs on the caller's thread.
    /// </para>
    /// </remarks>
    public static class DiskEncryption
    {
        /// <summary>Why the disks cannot be encrypted now, or null. The machine is assumed stopped.</summary>
        public static string? WhyCannotEncrypt(VmConfig config, Func<string, bool> isProtected)
        {
            if (config.DisksEncrypted)
                return EncryptionText.AlreadyEncrypted;
            return WhyNotRewritable(config, isProtected);
        }

        /// <summary>Why the disks cannot be decrypted now, or null.</summary>
        public static string? WhyCannotDecrypt(VmConfig config, Func<string, bool> isProtected) =>
            !config.DisksEncrypted ? EncryptionText.NotEncrypted : WhyNotRewritable(config, isProtected);

        /// <summary>
        /// Why the password cannot be changed now, or null. A linked clone and its original read the same images, so a
        /// new password on one side would lock the other out.
        /// </summary>
        public static string? WhyCannotChangePassword(VmConfig config, Func<string, bool> isProtected)
        {
            if (!config.DisksEncrypted)
                return EncryptionText.NotEncrypted;
            return LinkedProblem(config, isProtected);
        }

        private static string? WhyNotRewritable(VmConfig config, Func<string, bool> isProtected)
        {
            if (config.Disks.Count == 0)
                return EncryptionText.NoDisks;
            var others = config.Disks.Where(d => d.Format != DiskFormat.Qcow2).ToList();
            if (others.Count > 0)
            {
                return string.Format(CultureInfo.CurrentCulture, EncryptionText.NeedsQcow2Disks,
                    string.Join(", ", others.Select(d => $"{d.Id} ({d.Format.ToString().ToLowerInvariant()})")));
            }
            if (config.Disks.FirstOrDefault(d => !File.Exists(config.ResolvePath(d.FileName))) is { } missing)
                return string.Format(CultureInfo.CurrentCulture, EncryptionText.DiskMissing, missing.Id, config.ResolvePath(missing.FileName));
            return LinkedProblem(config, isProtected);
        }

        private static string? LinkedProblem(VmConfig config, Func<string, bool> isProtected)
        {
            if (config.Disks.Any(d => d.IsLinkedClone))
                return EncryptionText.RefusedLinkedClone;
            var images = config.Disks
                .SelectMany(d => d.ChainFromTop.Concat(d.RetainedImages.Select(r => r.FileName)))
                .Select(config.ResolvePath);
            return images.Any(isProtected) ? EncryptionText.RefusedHasLinkedClones : null;
        }

        /// <summary>
        /// The free space a rewrite needs on the machine's drive, roughly: what the disks' images take now. Null when
        /// it is there, otherwise what to tell the user.
        /// </summary>
        public static async Task<string?> WhyNotEnoughSpaceAsync(VmConfig config, QemuImgService images, CancellationToken cancellationToken = default)
        {
            long needed = 0;
            foreach (var disk in config.Disks)
            {
                foreach (var file in disk.ChainFromTop)
                {
                    var path = config.ResolvePath(file);
                    if (File.Exists(path))
                        needed += (await images.GetInfoAsync(path, cancellationToken)).ActualSizeBytes;
                }
            }

            var root = Path.GetPathRoot(Path.GetFullPath(config.FolderPath));
            if (root is null)
                return null;
            var free = new DriveInfo(root).AvailableFreeSpace;
            return free >= needed
                ? null
                : string.Format(CultureInfo.CurrentCulture, EncryptionText.NoSpace, Sizes.Format(needed), Sizes.Format(free));
        }

        /// <summary>
        /// Rewrites every disk encrypted with <paramref name="images"/>' password (<paramref name="encrypt"/> true), or
        /// in clear with the password it is opened with (false), then puts the copies in place of the old images.
        /// </summary>
        /// <param name="images">qemu-img with the machine's password, the new one when encrypting.</param>
        /// <param name="save">Writes the descriptor; called each time it changes, so an interruption leaves it right.</param>
        /// <remarks>
        /// Every copy is made before anything changes: a failure or a cancel deletes the copies and leaves the machine
        /// exactly as it was. Then, per disk: the descriptor points at the copy and is saved, the old chain is deleted
        /// (its images inside the machine's folder only), and the copy takes the disk's usual name.
        /// </remarks>
        public static async Task<DiskEncryptionResult> RewriteAsync(
            VmConfig config,
            QemuImgService images,
            bool encrypt,
            Action save,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (images.Key is null)
                throw new InvalidOperationException(EncryptionText.PasswordRequired);

            var sizes = new List<long>();
            foreach (var disk in config.Disks)
                sizes.Add(Math.Max(1, (await images.GetInfoAsync(config.ResolvePath(disk.FileName), cancellationToken)).VirtualSizeBytes));
            var total = (double)sizes.Sum();
            var done = 0d;

            var copies = new List<string>();
            try
            {
                for (var index = 0; index < config.Disks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var disk = config.Disks[index];
                    var copy = FreeName(config, $"{disk.Id}.{(encrypt ? "encrypting" : "decrypting")}");
                    copies.Add(copy);
                    var before = done;
                    var weight = sizes[index];
                    await images.ConvertAsync(
                        config.ResolvePath(disk.FileName),
                        config.ResolvePath(copy),
                        DiskFormat.Qcow2,
                        compress: false,
                        progress is null ? null : new InlineProgress(f => progress.Report((before + f * weight) / total)),
                        cancellationToken,
                        encryptDestination: encrypt);
                    done += weight;
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                foreach (var copy in copies)
                    TryDelete(config.ResolvePath(copy));
                throw;
            }

            // From here on the copies are the machine's disks.
            var left = new List<string>();
            config.DisksEncrypted = encrypt;
            // The snapshots are gone with the old images; the tree must not remember them.
            config.SnapshotLinks.Clear();
            config.CurrentSnapshot = string.Empty;
            for (var index = 0; index < config.Disks.Count; index++)
            {
                var disk = config.Disks[index];
                var old = disk.ChainFromTop.Select(config.ResolvePath).ToList();
                disk.FileName = copies[index];
                disk.BackingFileName = string.Empty;
                disk.DeeperBackingFiles = new List<string>();
                save();

                foreach (var path in old)
                {
                    if (IsInside(config.FolderPath, path))
                        TryDelete(path);
                    else
                        left.Add(path);
                }

                var final = FreeName(config, disk.Id);
                File.Move(config.ResolvePath(copies[index]), config.ResolvePath(final));
                disk.FileName = final;
                save();
            }

            progress?.Report(1d);
            return new DiskEncryptionResult(left);
        }

        /// <summary>
        /// Gives every image of the machine <paramref name="newKey"/> in place of the password <paramref name="images"/>
        /// has (qemu-img amend on the LUKS keyslots: nothing is rewritten, the snapshots stay).
        /// </summary>
        /// <remarks>
        /// In two passes so that no interruption leaves images under different passwords: the new password is first
        /// added to every image (both then open them; a failure removes it again from those done), then the old one is
        /// erased from each. Interrupted during the second pass, every image still opens with the new password.
        /// </remarks>
        public static async Task ChangePasswordAsync(VmConfig config, QemuImgService images, DiskKey newKey, CancellationToken cancellationToken = default)
        {
            var oldKey = images.Key ?? throw new InvalidOperationException(EncryptionText.PasswordRequired);
            if (oldKey.Password == newKey.Password)
                throw new InvalidOperationException(EncryptionText.PasswordUnchanged);

            var paths = new List<string>();
            foreach (var path in config.Disks
                         .SelectMany(d => d.ChainFromTop.Concat(d.RetainedImages.Select(r => r.FileName)))
                         .Select(config.ResolvePath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path) && await images.IsEncryptedAsync(path, cancellationToken))
                    paths.Add(path);
            }

            var added = new List<string>();
            try
            {
                foreach (var path in paths)
                {
                    await images.AddPasswordAsync(path, newKey, cancellationToken);
                    added.Add(path);
                }
            }
            catch
            {
                foreach (var path in added)
                {
                    try
                    {
                        await images.RemovePasswordAsync(path, newKey, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is QemuImgException or DiskPasswordException)
                    {
                        // Both passwords open this one; the old one still opens every image.
                    }
                }
                throw;
            }

            var renewed = images.WithKey(newKey);
            foreach (var path in paths)
                await renewed.RemovePasswordAsync(path, oldKey, cancellationToken);
        }

        private static bool IsInside(string folder, string path)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string FreeName(VmConfig config, string stem)
        {
            var candidate = stem + ".qcow2";
            for (var attempt = 2; File.Exists(config.ResolvePath(candidate)); attempt++)
                candidate = $"{stem}-{attempt.ToString(CultureInfo.InvariantCulture)}.qcow2";
            return candidate;
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
                // Left behind rather than failing an operation that has already succeeded.
            }
        }

        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _report;
            public InlineProgress(Action<double> report) => _report = report;
            public void Report(double value) => _report(value);
        }
    }
}
