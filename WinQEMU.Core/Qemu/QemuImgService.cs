using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    public sealed record DiskSnapshot(string Id, string Name, long VmStateSizeBytes, DateTimeOffset CreatedAt);

    public sealed record DiskImageInfo(
        string Path,
        string Format,
        long VirtualSizeBytes,
        long ActualSizeBytes,
        string? BackingFile,
        IReadOnlyList<DiskSnapshot> Snapshots);

    /// <summary>
    /// Wraps <c>qemu-img</c>. Everything here operates on images while the VM is stopped;
    /// live snapshot work goes through <see cref="QmpClient"/> instead.
    /// </summary>
    public sealed class QemuImgService
    {
        private readonly string _qemuImgPath;

        public QemuImgService(QemuInstallation installation)
        {
            _qemuImgPath = installation.QemuImgPath;
        }

        /// <summary>Creates a new image. qcow2 images are sparse, so the file starts small regardless of capacity.</summary>
        public async Task CreateAsync(
            string path,
            DiskFormat format,
            long capacityBytes,
            string? backingFile = null,
            CancellationToken cancellationToken = default)
        {
            if (File.Exists(path))
                throw new IOException($"The file already exists: {path}");

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var arguments = new List<string> { "create", "-f", FormatName(format) };

            if (!string.IsNullOrWhiteSpace(backingFile))
            {
                arguments.Add("-b");
                arguments.Add(backingFile);
                // qemu-img refuses a backing file without an explicit backing format.
                arguments.Add("-F");
                arguments.Add(FormatName(format));
            }

            arguments.Add(path);
            arguments.Add(capacityBytes.ToString(CultureInfo.InvariantCulture));

            await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        public async Task<DiskImageInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default)
        {
            var result = await RunAsync(
                new[] { "info", "--output=json", "--force-share", path },
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;

            var snapshots = new List<DiskSnapshot>();
            if (root.TryGetProperty("snapshots", out var snapshotArray))
            {
                foreach (var entry in snapshotArray.EnumerateArray())
                {
                    var seconds = entry.TryGetProperty("date-sec", out var dateSec) ? dateSec.GetInt64() : 0;
                    snapshots.Add(new DiskSnapshot(
                        entry.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                        entry.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        entry.TryGetProperty("vm-state-size", out var stateSize) ? stateSize.GetInt64() : 0,
                        DateTimeOffset.FromUnixTimeSeconds(seconds)));
                }
            }

            return new DiskImageInfo(
                path,
                root.TryGetProperty("format", out var format) ? format.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("virtual-size", out var virtualSize) ? virtualSize.GetInt64() : 0,
                root.TryGetProperty("actual-size", out var actualSize) ? actualSize.GetInt64() : 0,
                root.TryGetProperty("backing-filename", out var backing) ? backing.GetString() : null,
                snapshots);
        }

        /// <summary>
        /// Grows an image. Shrinking is intentionally not offered: qemu-img cannot know where the
        /// guest filesystem ends, so it would silently truncate live data.
        /// </summary>
        public async Task GrowAsync(string path, long newCapacityBytes, CancellationToken cancellationToken = default)
        {
            var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (newCapacityBytes <= info.VirtualSizeBytes)
            {
                throw new ArgumentException(
                    "A disk can only be grown. Shrinking would truncate the guest filesystem.",
                    nameof(newCapacityBytes));
            }

            await RunAsync(
                new[] { "resize", path, newCapacityBytes.ToString(CultureInfo.InvariantCulture) },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Converts between formats — the path for importing VMDK/VHDX images or compacting a qcow2.
        /// Reads through the whole backing chain, so the result is one standalone image holding the
        /// current contents, without the internal snapshots.
        /// </summary>
        /// <param name="progress">Receives the completed fraction, from 0 to 1.</param>
        public async Task ConvertAsync(
            string sourcePath,
            string destinationPath,
            DiskFormat destinationFormat,
            bool compress = false,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var arguments = new List<string> { "convert", "-p", "-O", FormatName(destinationFormat) };
            if (compress && destinationFormat == DiskFormat.Qcow2)
                arguments.Add("-c");
            arguments.Add(sourcePath);
            arguments.Add(destinationPath);
            await RunConvertAsync(arguments, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes the whole chain as a streamOptimized VMDK, the compressed, append-only form OVF packages
        /// carry and VMware, VirtualBox and Proxmox import.
        /// </summary>
        /// <param name="adapterType">What the descriptor records ("ide", "lsilogic"); the OVF's controller is what counts.</param>
        public Task ConvertToStreamOptimizedVmdkAsync(
            string sourcePath,
            string destinationPath,
            string adapterType,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            RunConvertAsync(
                new List<string>
                {
                    "convert", "-p", "-O", "vmdk", "-o", $"subformat=streamOptimized,adapter_type={adapterType}",
                    sourcePath, destinationPath
                },
                progress,
                cancellationToken);

        private async Task RunConvertAsync(List<string> arguments, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await RunAsync(
                arguments,
                cancellationToken,
                progress is null ? null : line =>
                {
                    if (ParseProgress(line) is { } fraction)
                        progress.Report(fraction);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// The first <paramref name="sectors"/> 512-byte sectors of what the guest sees, whatever the image
        /// format: <c>qemu-img dd</c> into a scratch raw file, read back and deleted.
        /// </summary>
        public async Task<byte[]> ReadStartAsync(string path, int sectors, CancellationToken cancellationToken = default)
        {
            var scratch = Path.Combine(Path.GetTempPath(), $"winqemu-{Guid.NewGuid():N}.raw");
            try
            {
                await RunAsync(
                    new[]
                    {
                        "dd", "-O", "raw", "bs=512",
                        $"count={sectors.ToString(CultureInfo.InvariantCulture)}",
                        $"if={path}", $"of={scratch}"
                    },
                    cancellationToken).ConfigureAwait(false);
                return await File.ReadAllBytesAsync(scratch, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    File.Delete(scratch);
                }
                catch (IOException)
                {
                    // A scratch file of a kilobyte in the temp folder is not worth failing over.
                }
            }
        }

        /// <summary>
        /// Reads one of the "    (12.34/100%)" lines that <c>-p</c> prints. Returns null for anything else.
        /// </summary>
        public static double? ParseProgress(string line)
        {
            var match = ProgressPattern.Match(line);
            if (!match.Success)
                return null;

            return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? Math.Clamp(percent / 100d, 0d, 1d)
                : null;
        }

        private static readonly Regex ProgressPattern = new(@"\((\d+(?:\.\d+)?)/100%\)", RegexOptions.CultureInvariant);

        /// <summary>
        /// Checks a snapshot name against what qemu-img can actually store. Returns null when the name
        /// is usable, otherwise the reason.
        /// </summary>
        /// <remarks>
        /// Non-ASCII is refused because qemu-img on Windows destroys it: the tool receives its arguments
        /// through the ANSI code page, so a name containing "à" is written into the qcow2 as a
        /// replacement character and read back corrupted. Verified by passing correct UTF-8 bytes
        /// straight to qemu-img from a shell -- it stored U+FFFD, so no encoding choice on our side
        /// can preserve the name. Refusing it up front beats accepting a name the user will not recognise.
        /// </remarks>
        public static string? ValidateSnapshotName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Le nom de l'instantané est obligatoire.";

            var trimmed = name.Trim();
            if (trimmed.Length > 128)
                return "Le nom ne peut pas dépasser 128 caractères.";

            var offending = trimmed.Where(c => c > 0x7E || c < 0x20).Distinct().ToArray();
            if (offending.Length > 0)
            {
                return "qemu-img ne conserve pas les caractères accentués ou spéciaux dans un nom " +
                       "d'instantané : utilisez des lettres non accentuées, des chiffres, espaces ou tirets.";
            }

            return null;
        }

        public Task CreateOfflineSnapshotAsync(string path, string name, CancellationToken cancellationToken = default)
        {
            if (ValidateSnapshotName(name) is { } problem)
                throw new ArgumentException(problem, nameof(name));

            return RunAsync(new[] { "snapshot", "-c", name.Trim(), path }, cancellationToken);
        }

        /// <summary>
        /// Folds an overlay into the image it sits on, then the overlay can be discarded.
        /// </summary>
        /// <remarks>
        /// The counterpart of a live snapshot: committing keeps everything written since and abandons
        /// the ability to go back, whereas deleting the overlay keeps the old state and abandons
        /// everything since.
        /// </remarks>
        public Task CommitAsync(string overlayPath, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "commit", overlayPath }, cancellationToken);

        /// <summary>
        /// Points <paramref name="path"/> at a different backing image without copying anything
        /// (<c>rebase -u</c>). Only correct when the new base already holds the same data as the old one,
        /// which is what committing the old base into it guarantees.
        /// </summary>
        public Task RebaseUnsafeAsync(string path, string backingFile, DiskFormat backingFormat, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "rebase", "-u", "-b", backingFile, "-F", FormatName(backingFormat), path }, cancellationToken);

        public Task DeleteOfflineSnapshotAsync(string path, string name, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "snapshot", "-d", name, path }, cancellationToken);

        public Task ApplyOfflineSnapshotAsync(string path, string name, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "snapshot", "-a", name, path }, cancellationToken);

        private async Task<ProcessResult> RunAsync(
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            Action<string>? onOutputLine = null)
        {
            var argumentList = arguments.ToList();
            var result = await ProcessRunner.RunAsync(_qemuImgPath, argumentList, cancellationToken, onOutputLine).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new QemuImgException(
                    $"qemu-img {argumentList[0]} failed (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}): {result.Diagnostics}");
            }

            return result;
        }

        private static string FormatName(DiskFormat format) => format switch
        {
            DiskFormat.Qcow2 => "qcow2",
            DiskFormat.Raw => "raw",
            DiskFormat.Vhdx => "vhdx",
            DiskFormat.Vmdk => "vmdk",
            DiskFormat.Vdi => "vdi",
            _ => "qcow2"
        };
    }

    public sealed class QemuImgException : Exception
    {
        public QemuImgException(string message) : base(message) { }
    }
}
