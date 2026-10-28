using System.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Reads one small file out of an ISO image: enough to learn an installation disc's language
    /// (Windows' sources\lang.ini) without mounting it, which takes rights.
    /// </summary>
    /// <remarks>
    /// UDF first: Microsoft's ISOs are UDF only, their ISO 9660 tree holding nothing but a README that
    /// says so. Then ISO 9660 (names in upper case with a ";1" suffix), primary tree only, never Joliet.
    /// </remarks>
    public static class IsoFileReader
    {
        private const int Sector = 2048;

        /// <summary>The file at <paramref name="path"/> ("sources/lang.ini"), or null when the image has no such file.</summary>
        public static byte[]? Read(string isoPath, string path, int maxBytes = 1024 * 1024)
        {
            try
            {
                using var iso = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (ReadUdf(iso, path, maxBytes) is { } udf)
                    return udf;

                var descriptor = ReadSectors(iso, 16, 1);
                if (descriptor[0] != 1 || Encoding.ASCII.GetString(descriptor, 1, 5) != "CD001")
                    return null;

                // The root directory record sits at offset 156 of the primary volume descriptor.
                var (extent, length, isDirectory) = Record(descriptor, 156);
                foreach (var part in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!isDirectory || Find(iso, extent, length, part) is not { } entry)
                        return null;
                    (extent, length, isDirectory) = entry;
                }
                if (isDirectory || length > maxBytes)
                    return null;

                var data = ReadSectors(iso, extent, (int)((length + Sector - 1) / Sector));
                return data[..(int)length];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        // ---- UDF: Microsoft's own ISOs are UDF only; their ISO 9660 tree holds nothing but a README ----

        /// <summary>A file read through the image's UDF file system (ECMA-167), or null.</summary>
        private static byte[]? ReadUdf(FileStream iso, string path, int maxBytes)
        {
            // Anchor volume descriptor pointer at sector 256, tag 2: where the volume descriptors are.
            var anchor = ReadSectors(iso, 256, 1);
            if (Tag(anchor, 0) != 2)
                return null;
            var sequence = BitConverter.ToUInt32(anchor, 20);
            var sequenceLength = BitConverter.ToUInt32(anchor, 16);

            uint partitionStart = 0;
            uint fileSetBlock = 0;
            var found = 0;
            for (uint i = 0; i < Math.Min(sequenceLength / Sector, 64); i++)
            {
                var descriptor = ReadSectors(iso, sequence + i, 1);
                switch (Tag(descriptor, 0))
                {
                    case 5:   // partition descriptor
                        partitionStart = BitConverter.ToUInt32(descriptor, 188);
                        found |= 1;
                        break;
                    case 6:   // logical volume descriptor: the file set descriptor is its contents use
                        fileSetBlock = BitConverter.ToUInt32(descriptor, 252);
                        found |= 2;
                        break;
                    case 8:   // terminating descriptor
                        i = uint.MaxValue - 1;
                        break;
                }
            }
            if (found != 3)
                return null;

            var fileSet = ReadSectors(iso, partitionStart + fileSetBlock, 1);
            if (Tag(fileSet, 0) != 256)
                return null;
            var entry = BitConverter.ToUInt32(fileSet, 404);   // root directory ICB, a long_ad

            foreach (var part in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = ReadUdfFile(iso, partitionStart, entry, 16 * 1024 * 1024);
                if (directory is null || FindUdf(directory, part) is not { } child)
                    return null;
                entry = child;
            }
            return ReadUdfFile(iso, partitionStart, entry, maxBytes);
        }

        private static ushort Tag(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);

        /// <summary>The data of a file or directory, from its (extended) file entry's allocation descriptors.</summary>
        private static byte[]? ReadUdfFile(FileStream iso, uint partitionStart, uint block, int maxBytes)
        {
            var header = ReadSectors(iso, partitionStart + block, 1);
            var tag = Tag(header, 0);
            if (tag is not (261 or 266))
                return null;
            var length = BitConverter.ToUInt64(header, 56);
            if (length > (ulong)maxBytes)
                return null;
            var extended = tag == 266;
            var extendedAttributes = BitConverter.ToInt32(header, extended ? 208 : 168);
            var descriptorsLength = BitConverter.ToInt32(header, extended ? 212 : 172);
            var descriptors = (extended ? 216 : 176) + extendedAttributes;
            var type = BitConverter.ToUInt16(header, 16 + 18) & 7;

            if (type == 3)   // data embedded in the entry itself
                return header.AsSpan(descriptors, (int)Math.Min(length, (ulong)descriptorsLength)).ToArray();

            var data = new byte[length];
            var written = 0;
            var size = type == 0 ? 8 : 16;   // short_ad or long_ad
            for (var at = descriptors; at + size <= descriptors + descriptorsLength && written < data.Length; at += size)
            {
                var extentLength = (int)(BitConverter.ToUInt32(header, at) & 0x3FFFFFFF);
                var position = BitConverter.ToUInt32(header, at + 4);
                if (extentLength == 0)
                    break;
                var chunk = ReadSectors(iso, partitionStart + position, (extentLength + Sector - 1) / Sector);
                var take = Math.Min(extentLength, data.Length - written);
                Array.Copy(chunk, 0, data, written, take);
                written += take;
            }
            return data;
        }

        /// <summary>The ICB block of a directory's child, by name, case-insensitively.</summary>
        private static uint? FindUdf(byte[] directory, string name)
        {
            var offset = 0;
            while (offset + 38 <= directory.Length && Tag(directory, offset) == 257)
            {
                var nameLength = directory[offset + 19];
                var implementationUse = BitConverter.ToUInt16(directory, offset + 36);
                var icb = BitConverter.ToUInt32(directory, offset + 24);
                var nameStart = offset + 38 + implementationUse;
                if (nameLength > 0 && (directory[offset + 18] & 8) == 0)   // not the parent entry
                {
                    var compression = directory[nameStart];
                    var entryName = compression == 16
                        ? Encoding.BigEndianUnicode.GetString(directory, nameStart + 1, nameLength - 1)
                        : Encoding.Latin1.GetString(directory, nameStart + 1, nameLength - 1);
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                        return icb;
                }
                offset += (38 + implementationUse + nameLength + 3) & ~3;
            }
            return null;
        }

        private static (uint Extent, uint Length, bool IsDirectory)? Find(FileStream iso, uint extent, uint length, string name)
        {
            var directory = ReadSectors(iso, extent, (int)((length + Sector - 1) / Sector));
            var offset = 0;
            while (offset < length)
            {
                var size = directory[offset];
                if (size == 0)
                {
                    // Records never straddle sectors: a zero length means the rest of this sector is padding.
                    offset = (offset / Sector + 1) * Sector;
                    continue;
                }
                var nameLength = directory[offset + 32];
                var entryName = Encoding.ASCII.GetString(directory, offset + 33, nameLength);
                var bare = entryName.Split(';')[0].TrimEnd('.');
                if (string.Equals(bare, name, StringComparison.OrdinalIgnoreCase))
                    return Record(directory, offset);
                offset += size;
            }
            return null;
        }

        /// <summary>A directory record's extent, data length and directory flag (little-endian halves of the both-endian fields).</summary>
        private static (uint, uint, bool) Record(byte[] data, int offset) =>
            (BitConverter.ToUInt32(data, offset + 2), BitConverter.ToUInt32(data, offset + 10), (data[offset + 25] & 2) != 0);

        private static byte[] ReadSectors(FileStream iso, uint first, int count)
        {
            var buffer = new byte[Math.Max(1, count) * Sector];
            iso.Seek((long)first * Sector, SeekOrigin.Begin);
            iso.ReadExactly(buffer);
            return buffer;
        }

        /// <summary>
        /// The language of a Windows installation disc ("fr-FR"), from sources\lang.ini; null when the
        /// image is not one, or says nothing.
        /// </summary>
        public static string? WindowsLanguage(string isoPath)
        {
            if (Read(isoPath, "sources/lang.ini") is not { } bytes)
                return null;
            var inLanguages = false;
            foreach (var raw in Encoding.ASCII.GetString(bytes).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inLanguages = line.Equals("[Available UI Languages]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inLanguages && line.Contains('='))
                    return line.Split('=')[0].Trim();
            }
            return null;
        }
    }
}
