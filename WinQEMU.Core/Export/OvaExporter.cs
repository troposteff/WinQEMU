using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinQEMU.Core.Import;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Export
{
    /// <summary>
    /// Exports a stopped machine as an .ova, as VMware's "Exporter au format OVF" does: the descriptor
    /// (<see cref="OvfWriter"/>), a SHA-256 manifest, then each disk as a streamOptimized VMDK, in that order
    /// inside a tar, since OVF 1.0 wants the descriptor first and the manifest right after it.
    /// </summary>
    /// <remarks>
    /// The manifest has to precede the disks, so each disk is converted to a work file next to the package
    /// first, hashed, then copied into the tar and deleted: the destination's volume needs room for about
    /// twice the compressed disks while it runs. The machine's current state is exported, its whole overlay
    /// chain (and a linked clone's base) read by qemu-img; snapshots stay behind, as with VMware.
    /// </remarks>
    public static class OvaExporter
    {
        /// <summary>Why this machine cannot be exported at all, or null.</summary>
        public static string? WhyCannotExport(VmConfig config)
        {
            if (config.Architecture != GuestArchitecture.X86_64)
                return "Seules les machines x86-64 s'exportent : VMware et VirtualBox n'exécutent pas d'autre architecture.";
            if (config.Disks.Count == 0)
                return "Cette machine n'a aucun disque à exporter.";
            return config.Disks.Select(d => config.ResolvePath(d.FileName)).FirstOrDefault(p => !File.Exists(p)) is { } missing
                ? $"Disque introuvable : {missing}"
                : null;
        }

        /// <summary>What will not come across as it is here, phrased for the dialog.</summary>
        public static IReadOnlyList<string> Notes(VmConfig config)
        {
            var notes = new List<string>();
            var family = GuestOsCatalog.Find(config.GuestOs)?.Family;

            if (config.Disks.Any(d => d.Bus == DiskBus.VirtioBlk))
            {
                notes.Add(family switch
                {
                    "Windows" => "Les disques VirtIO deviennent SATA, VirtIO n'existant ni chez VMware ni chez VirtualBox. " +
                                 "Windows ne démarre sur SATA que si son pilote AHCI démarre avec lui : avant d'exporter, " +
                                 "lancez dans la machine sc config storahci start= boot, sinon INACCESSIBLE_BOOT_DEVICE.",
                    "Linux" => "Les disques VirtIO deviennent SATA, VirtIO n'existant ni chez VMware ni chez VirtualBox. " +
                               "Un Linux dont l'initramfs n'a que les pilotes en usage (dracut en mode hostonly) s'arrêtera en " +
                               "« emergency mode » : avant d'exporter, lancez dans la machine echo 'hostonly=\"no\"' | sudo tee " +
                               "/etc/dracut.conf.d/90-generic.conf puis sudo dracut --force --regenerate-all.",
                    _ => "Les disques VirtIO deviennent SATA, VirtIO n'existant ni chez VMware ni chez VirtualBox."
                });
            }

            if (config.NetworkAdapters.Any(a => a.Mode != NetworkMode.None && a.Model is NicModel.VirtioNet or NicModel.Rtl8139))
            {
                notes.Add(family == "Windows"
                    ? "Les cartes réseau VirtIO deviennent des Intel e1000e, dont Windows a le pilote."
                    : "Les cartes réseau VirtIO deviennent des Intel e1000, dont tous les systèmes ont le pilote.");
            }

            if (config.NetworkAdapters.Any(a => a.Mode != NetworkMode.None))
                notes.Add("Les cartes réseau sont branchées sur un réseau nommé « nat » : l'hyperviseur qui importe demande où les brancher.");

            if (config.Firmware == FirmwareType.Uefi)
            {
                notes.Add("Machine UEFI : VMware le lit dans le paquet ; dans VirtualBox, cochez « Activer EFI » si l'import ne l'a pas fait. " +
                          "Les entrées de démarrage ne suivent pas : le microprogramme démarre \\EFI\\BOOT\\BOOTX64.EFI.");
            }

            if (config.Disks.Any(d => d.IsOverlay || d.IsLinkedClone) || config.SnapshotLinks.Count > 0)
                notes.Add("Seul l'état actuel est exporté : les instantanés restent ici.");

            notes.Add("Les dossiers partagés, l'accès depuis le réseau et les périphériques USB ne suivent pas.");
            return notes;
        }

        /// <summary>A name for the files inside the package: ASCII, no separators, at most 60 characters.</summary>
        public static string PackageBaseName(string machineName)
        {
            var folded = new string(machineName.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
                .ToArray()).Trim('-', '.');
            if (folded.Length > 60)
                folded = folded[..60].TrimEnd('-', '.');
            return folded.Length == 0 ? "machine" : folded;
        }

        /// <param name="progress">Receives the completed fraction, from 0 to 1.</param>
        public static async Task ExportAsync(
            VmConfig config,
            string destination,
            QemuImgService images,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (WhyCannotExport(config) is { } problem)
                throw new InvalidOperationException(problem);

            destination = Path.GetFullPath(destination);
            var baseName = PackageBaseName(config.Name);
            // The disk the machine boots from first: importers boot the first disk of the descriptor.
            var disks = config.Disks
                .OrderBy(d => d.BootIndex < 0 ? int.MaxValue : d.BootIndex)
                .Select((d, i) => (
                    Source: config.ResolvePath(d.FileName),
                    Name: $"{baseName}-disk{(i + 1).ToString(CultureInfo.InvariantCulture)}.vmdk",
                    d.CapacityBytes,
                    Bus: OvfWriter.BusFor(d.Bus)))
                .ToList();

            // Next to the package, on the same volume: converting into the temp folder of C: would fail on a
            // full system drive while the destination had room.
            var work = destination + ".parts";
            Directory.CreateDirectory(work);
            var completed = false;
            try
            {
                // Conversion is most of the time (compression); hashing and copying share the rest.
                var total = (double)disks.Sum(d => Math.Max(1, d.CapacityBytes));
                var done = 0d;
                var exported = new List<ExportedDisk>();
                foreach (var disk in disks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var weight = Math.Max(1, disk.CapacityBytes);
                    var before = done;
                    await images.ConvertToStreamOptimizedVmdkAsync(
                        disk.Source,
                        Path.Combine(work, disk.Name),
                        disk.Bus is ExportBus.Scsi or ExportBus.ScsiParavirtual ? "lsilogic" : "ide",
                        progress is null ? null : new InlineProgress(f => progress.Report(0.85 * (before + f * weight) / total)),
                        cancellationToken).ConfigureAwait(false);
                    done += weight;

                    var info = await images.GetInfoAsync(disk.Source, cancellationToken).ConfigureAwait(false);
                    exported.Add(new ExportedDisk(disk.Name, new FileInfo(Path.Combine(work, disk.Name)).Length,
                        info.VirtualSizeBytes > 0 ? info.VirtualSizeBytes : disk.CapacityBytes, disk.Bus));
                }

                var ovfName = baseName + ".ovf";
                var ovf = OvfWriter.Serialize(OvfWriter.Build(config, exported));

                var manifest = new StringBuilder();
                manifest.Append(CultureInfo.InvariantCulture, $"SHA256({ovfName})= {Convert.ToHexString(SHA256.HashData(ovf)).ToLowerInvariant()}\n");
                var bytesTotal = (double)Math.Max(1, exported.Sum(d => d.FileSize));
                var bytesDone = 0L;
                foreach (var disk in exported)
                {
                    await using var stream = new FileStream(Path.Combine(work, disk.FileName), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
                    var hash = await HashAsync(stream, read =>
                    {
                        bytesDone += read;
                        progress?.Report(0.85 + 0.05 * bytesDone / bytesTotal);
                    }, cancellationToken).ConfigureAwait(false);
                    manifest.Append(CultureInfo.InvariantCulture, $"SHA256({disk.FileName})= {hash}\n");
                }

                bytesDone = 0;
                var modified = DateTimeOffset.UtcNow;
                await using (var package = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    WriteFile(package, ovfName, ovf, modified);
                    WriteFile(package, baseName + ".mf", Encoding.ASCII.GetBytes(manifest.ToString()), modified);

                    foreach (var disk in exported)
                    {
                        var path = Path.Combine(work, disk.FileName);
                        TarArchive.WriteHeader(package, disk.FileName, disk.FileSize, modified);
                        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
                        {
                            await CopyAsync(source, package, read =>
                            {
                                bytesDone += read;
                                progress?.Report(0.9 + 0.1 * bytesDone / bytesTotal);
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        TarArchive.WritePadding(package, disk.FileSize);
                        // Gone as soon as it is in the package: the volume then holds a little less at once.
                        File.Delete(path);
                    }

                    TarArchive.WriteEnd(package);
                }

                completed = true;
                progress?.Report(1d);
            }
            finally
            {
                TryDelete(() => Directory.Delete(work, recursive: true));
                if (!completed)
                    TryDelete(() => File.Delete(destination));
            }
        }

        private static void WriteFile(Stream package, string name, byte[] content, DateTimeOffset modified)
        {
            TarArchive.WriteHeader(package, name, content.Length, modified);
            package.Write(content);
            TarArchive.WritePadding(package, content.Length);
        }

        private static async Task<string> HashAsync(Stream stream, Action<int> onRead, CancellationToken cancellationToken)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                onRead(read);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        private static async Task CopyAsync(Stream source, Stream destination, Action<int> onRead, CancellationToken cancellationToken)
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onRead(read);
            }
        }

        private static void TryDelete(Action delete)
        {
            try
            {
                delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind rather than hiding the export's own outcome.
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
