using System.Globalization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>What a compaction left: the file's size before and after.</summary>
    public sealed record CompactionResult(long BytesBefore, long BytesAfter);

    /// <summary>
    /// Gives back the space a qcow2 disk no longer needs, as VMware's "Compacter": a qcow2 file only grows,
    /// even once the guest has deleted files. Rewritten by qemu-img convert, which leaves out the clusters that
    /// read as zeros or were discarded, then put in place of the original. The guest's free space reads as
    /// zeros only once trimmed (see TrimAsync), on a disk whose discards reach the image (DiskConfig.Discard).
    /// </summary>
    /// <remarks>
    /// Refused where a rewrite would lose something or break something: internal snapshots (convert does not
    /// copy them); an overlay or a linked clone's disk (convert would read the whole chain into one file, and
    /// the snapshot or the link would be gone); an image a linked clone reads (it must never change).
    /// The copy is written next to the disk, on the same volume, so the replacement is a rename.
    /// </remarks>
    public static class DiskCompaction
    {
        /// <summary>Why this disk cannot be compacted, or null. The image is read for its snapshots and size.</summary>
        public static string? WhyNot(VmConfig config, DiskConfig disk, DiskImageInfo info, Func<string, bool> isProtected)
        {
            if (disk.Format != DiskFormat.Qcow2)
                return CoreText.CompactionQcow2Only;
            if (disk.IsOverlay)
                return CoreText.CompactionRefusedOverlay;
            if (disk.IsLinkedClone)
                return CoreText.CompactionRefusedLinkedClone;
            var path = config.ResolvePath(disk.FileName);
            if (isProtected(path))
                return CoreText.CompactionRefusedProtected;
            if (info.Snapshots.Count > 0)
                return string.Format(CultureInfo.CurrentCulture, CoreText.CompactionRefusedSnapshots, info.Snapshots.Count);
            // DriveInfo refuses a network path (ArgumentException), which took WinQEMU down from the button: unknown, not refused.
            var free = HostCapabilities.GetFreeSpaceBytes(path);
            if (free >= 0 && free < info.ActualSizeBytes)
                return CoreText.CompactionNoSpace;
            return null;
        }

        /// <summary>Rewrites the image without its unused clusters and puts the copy in its place.</summary>
        public static async Task<CompactionResult> CompactAsync(QemuImgService images, string path, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var before = new FileInfo(path).Length;
            var copy = path + ".compact";
            var original = path + ".precompact";
            if (File.Exists(copy))
                File.Delete(copy);
            try
            {
                await images.ConvertAsync(path, copy, DiskFormat.Qcow2, compress: false, progress, cancellationToken).ConfigureAwait(false);
                // With a backup name: ReplaceFile without one can fail after the original is gone (the copy held by an
                // antivirus: ERROR_UNABLE_TO_MOVE_REPLACEMENT), and the copy, deleted below, was then the only one left.
                // The original is only renamed (same volume), then deleted once the copy is in place.
                File.Replace(copy, path, original, ignoreMetadataErrors: true);
            }
            catch
            {
                // Put back whatever happened; a copy is deleted only while the disk itself is there.
                if (!File.Exists(path) && File.Exists(original))
                    File.Move(original, path);
                else if (!File.Exists(path) && File.Exists(copy))
                    File.Move(copy, path);
                if (File.Exists(path) && File.Exists(copy))
                    File.Delete(copy);
                throw;
            }
            try
            {
                File.Delete(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held a moment: left beside the disk, its name says what it is.
            }
            return new CompactionResult(before, new FileInfo(path).Length);
        }

        /// <summary>
        /// Asks the guest to tell its disks which blocks are free (guest-fstrim; on Windows the agent runs
        /// "defrag /L"). Returns how many filesystems answered, or throws GuestAgentException.
        /// </summary>
        public static async Task<int> TrimAsync(GuestAgentClient agent, CancellationToken cancellationToken = default)
        {
            var result = await agent.ExecuteAsync("guest-fstrim", null, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            return result?["paths"]?.AsArray().Count ?? 0;
        }
    }
}
