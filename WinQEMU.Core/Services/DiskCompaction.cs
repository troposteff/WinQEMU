using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

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
                return "Seul un disque qcow2 se compacte.";
            if (disk.IsOverlay)
                return "Ce disque porte des instantanés à chaud : supprimez-les ou fusionnez-les d'abord, sinon ils seraient perdus.";
            if (disk.IsLinkedClone)
                return "C'est le disque d'un clone lié : le compacter recopierait l'image dont il dépend. Rendez-le d'abord indépendant (« Convertir… »).";
            var path = config.ResolvePath(disk.FileName);
            if (isProtected(path))
                return "Un clone lié lit cette image : elle ne doit plus jamais changer.";
            if (info.Snapshots.Count > 0)
                return $"Ce disque porte {info.Snapshots.Count} instantané(s) interne(s), que le compactage ne garderait pas : supprimez-les d'abord.";
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not null && new DriveInfo(root).AvailableFreeSpace < info.ActualSizeBytes)
                return "Il faut sur le volume du disque autant de place libre que le disque en occupe, pour la copie.";
            return null;
        }

        /// <summary>Rewrites the image without its unused clusters and puts the copy in its place.</summary>
        public static async Task<CompactionResult> CompactAsync(QemuImgService images, string path, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var before = new FileInfo(path).Length;
            var copy = path + ".compact";
            if (File.Exists(copy))
                File.Delete(copy);
            try
            {
                await images.ConvertAsync(path, copy, DiskFormat.Qcow2, compress: false, progress, cancellationToken).ConfigureAwait(false);
                File.Replace(copy, path, destinationBackupFileName: null);
            }
            catch
            {
                if (File.Exists(copy))
                    File.Delete(copy);
                throw;
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
