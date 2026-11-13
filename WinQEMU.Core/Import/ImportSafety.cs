using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Import
{
    /// <summary>
    /// What an imported machine may make WinQEMU open. A .vmx, .vbox, .ovf or .ova comes from elsewhere (a download, a
    /// mail), and what it names was opened as it was written: a disk on another computer (\\host\share\…) made Windows
    /// authenticate there, handing over the user's NTLM hash, as soon as the file was chosen, before any confirmation;
    /// and an image may itself name other files, which qemu-img reads into the new guest's disk: a qcow2 backing file or
    /// external data file, a VMDK extent or parent, set to a file of this PC (C:\Users\…\Documents\…), put that file's
    /// bytes inside a disk the guest can read and send away on its first boot.
    /// </summary>
    /// <remarks>
    /// Everything here is read from the files' own headers, never through qemu-img, which would already open what they
    /// name. A path the user chose on a network share stays usable: what lies in the same folder is the user's choice too.
    /// </remarks>
    public static class ImportSafety
    {
        /// <summary>A network path (UNC) or a device path; "\\?\C:\…" is a local long path.</summary>
        public static bool IsRemote(string path)
        {
            var p = path.Replace('/', '\\');
            if (!p.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            return !(p.Length >= 7 && p.StartsWith(@"\\?\", StringComparison.Ordinal) && p[5] == ':' && char.IsLetter(p[4]));
        }

        /// <summary>Whether <paramref name="path"/> lies inside <paramref name="folder"/> (or is the folder itself).</summary>
        public static bool IsWithin(string path, string folder)
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a file named by the configuration <paramref name="source"/> may be opened: anything local, and on the
        /// network only what lies in the configuration's own folder.
        /// </summary>
        public static bool MayOpen(string path, string source) =>
            !IsRemote(path) || IsWithin(path, Path.GetDirectoryName(Path.GetFullPath(source))!);

        /// <summary>The message for a file a configuration names on another computer.</summary>
        public static string RemoteMessage(string path) =>
            string.Format(CultureInfo.CurrentCulture, PlatformText.ImportRemotePath, path);

        /// <summary>
        /// Why the disk must not be converted, or null: an image that would make qemu-img read files other than its own
        /// (see the class). An image's references are kept to its own folder, as VMware's snapshot deltas are; one inside
        /// a package may reference nothing.
        /// </summary>
        public static string? WhyUnsafe(ImportedDisk disk)
        {
            try
            {
                return Check(disk.SourcePath, disk.PackageWindow, depth: 0);
            }
            catch (IOException)
            {
                // Unreadable here: qemu-img will say so in its own words.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string? Check(string path, TarEntry? window, int depth)
        {
            if (depth > 16)
                return References(path, PlatformText.ImportChainTooDeep);
            var head = Read(path, window, 0, 4096);
            if (head.Length < 4)
                return null;
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var name = window?.Name ?? Path.GetFileName(path);

            // qcow2: "QFI\xfb", backing file name at the offset and size of the header, external data file a feature bit.
            if (head[0] == 'Q' && head[1] == 'F' && head[2] == 'I' && head[3] == 0xFB && head.Length >= 104)
            {
                var version = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                if (version >= 3 && (BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(72)) & 0b100) != 0)
                    return References(name, PlatformText.ImportExternalDataFile);
                var backingOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
                var backingSize = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16));
                if (backingSize == 0)
                    return null;
                if (backingSize > 1023 || backingOffset <= 0)
                    return References(name, "?");
                var backing = Encoding.UTF8.GetString(Read(path, window, backingOffset, backingSize));
                return Referenced(path, window, folder, name, backing, depth);
            }

            // VMDK, sparse ("KDMV"): an embedded descriptor, whose parent qemu opens; its extent is the file itself.
            if (head[0] == 'K' && head[1] == 'D' && head[2] == 'M' && head[3] == 'V' && head.Length >= 44)
            {
                var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(28)) * 512;
                var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(36)) * 512;
                if (offset <= 0 || size <= 0)
                    return null;
                var text = Encoding.UTF8.GetString(Read(path, window, offset, (int)Math.Min(size, 1 << 20)));
                return Parent(text) is { } parent ? Referenced(path, window, folder, name, parent, depth) : null;
            }

            // VMDK, text descriptor: extents and parent, all opened by qemu.
            var start = Encoding.ASCII.GetString(head, 0, Math.Min(head.Length, 1024));
            if (start.Contains("# Disk DescriptorFile", StringComparison.Ordinal) || start.Contains("createType", StringComparison.Ordinal))
            {
                if (window is not null)
                    return References(name, PlatformText.ImportPackageDescriptor);
                var text = Encoding.UTF8.GetString(Read(path, null, 0, 1 << 20));
                foreach (Match extent in Extent.Matches(text))
                {
                    if (extent.Groups["file"].Success
                        && Referenced(path, null, folder, name, extent.Groups["file"].Value, depth, followed: false) is { } why)
                        return why;
                }
                return Parent(text) is { } parent ? Referenced(path, null, folder, name, parent, depth) : null;
            }

            // VHD, VHDX, VDI, raw: QEMU opens no other file for them.
            return null;
        }

        /// <summary>A file an image names: inside its own folder, never inside a package; then checked in turn.</summary>
        private static string? Referenced(string path, TarEntry? window, string folder, string name, string reference, int depth,
            bool followed = true)
        {
            if (window is not null)
                return References(name, reference);
            if (reference.StartsWith("json:", StringComparison.OrdinalIgnoreCase) || reference.Contains("://", StringComparison.Ordinal))
                return References(name, reference);
            var target = Path.IsPathRooted(reference) ? reference : Path.Combine(folder, reference);
            if (IsRemote(target) || !IsWithin(target, folder))
                return References(name, reference);
            if (!followed || !File.Exists(target))
                return null;
            return Check(target, null, depth + 1);
        }

        private static string References(string name, string reference) =>
            string.Format(CultureInfo.CurrentCulture, PlatformText.ImportImageReferences, name, reference);

        private static string? Parent(string descriptor) =>
            ParentHint.Match(descriptor) is { Success: true } match && match.Groups["file"].Value.Length > 0
                ? match.Groups["file"].Value
                : null;

        private static readonly Regex Extent = new(
            @"^\s*(RW|RDONLY|NOACCESS)\s+\d+\s+\w+(\s+""(?<file>[^""]*)"")?",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex ParentHint = new(
            @"^\s*parentFileNameHint\s*=\s*""(?<file>[^""]*)""",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>Up to <paramref name="count"/> bytes at <paramref name="offset"/>, of the file or of its window in a package.</summary>
        private static byte[] Read(string path, TarEntry? window, long offset, int count)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var limit = window?.Size ?? stream.Length;
            if (offset < 0 || offset >= limit)
                return Array.Empty<byte>();
            count = (int)Math.Min(count, limit - offset);
            stream.Position = (window?.DataOffset ?? 0) + offset;
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0)
                    break;
                total += read;
            }
            return total == count ? buffer : buffer[..total];
        }
    }
}
