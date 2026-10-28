using System.Globalization;
using System.Text;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Import
{
    /// <summary>A file stored in a tar archive: where its bytes start and how many there are.</summary>
    public sealed record TarEntry(string Name, long DataOffset, long Size);

    /// <summary>
    /// The tar format of .ova packages, read and written here rather than by System.Formats.Tar: reading
    /// needs each file's offset, so QEMU can open a disk inside the package where it lies (raw driver,
    /// offset and size) instead of extracting tens of gigabytes; writing needs plain ustar headers.
    /// </summary>
    /// <remarks>
    /// A disk larger than 8 GiB does not fit ustar's 11 octal digits. Its size is then written in GNU's
    /// base-256 form, which GNU tar, VirtualBox, 7-Zip and Python read, rather than in a PAX extended header,
    /// which a reader that does not know PAX takes for an extra file named "PaxHeader/…".
    /// </remarks>
    public static class TarArchive
    {
        private const int Block = 512;

        /// <summary>The regular files of an archive, in order. Throws <see cref="FormatException"/> on a non-tar file.</summary>
        public static IReadOnlyList<TarEntry> ReadEntries(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadEntries(stream);
        }

        public static IReadOnlyList<TarEntry> ReadEntries(Stream stream)
        {
            var entries = new List<TarEntry>();
            var header = new byte[Block];
            string? longName = null;
            long? paxSize = null;
            string? paxPath = null;

            while (true)
            {
                var headerOffset = stream.Position;
                if (ReadFully(stream, header) < Block)
                    break;
                if (header.All(b => b == 0))
                    break;
                if (!ChecksumMatches(header))
                    throw new FormatException(entries.Count == 0
                        ? PlatformText.ImportTarInvalid
                        : string.Format(CultureInfo.CurrentCulture, PlatformText.ImportTarDamaged, entries[^1].Name));

                var size = ParseNumber(header.AsSpan(124, 12));
                var type = (char)header[156];
                var dataOffset = headerOffset + Block;

                switch (type)
                {
                    case 'L':
                        // GNU long name: the data block holds the next entry's name.
                        longName = ReadText(stream, size).TrimEnd('\0');
                        break;
                    case 'x':
                        // PAX extended header for the next entry: "<length> key=value\n" records.
                        foreach (var (key, value) in ParsePax(ReadText(stream, size)))
                        {
                            if (key == "path")
                                paxPath = value;
                            else if (key == "size" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                                paxSize = s;
                        }
                        break;
                    case 'g':
                        break;
                    default:
                        size = paxSize ?? size;
                        if (type is '0' or '\0' or '7')
                        {
                            var name = paxPath ?? longName ?? HeaderName(header);
                            entries.Add(new TarEntry(name, dataOffset, size));
                        }
                        longName = null;
                        paxPath = null;
                        paxSize = null;
                        break;
                }

                // A truncated archive ends here; qemu-img then says what it cannot read.
                var next = dataOffset + RoundUp(size);
                if (next >= stream.Length)
                    break;
                stream.Position = next;
            }

            return entries;
        }

        /// <summary>Writes the header of a regular file; its <paramref name="size"/> bytes and padding follow.</summary>
        public static void WriteHeader(Stream stream, string name, long size, DateTimeOffset modified)
        {
            var header = new byte[Block];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 100)
                throw new ArgumentException("A name in the package is limited to 100 bytes.", nameof(name));
            nameBytes.CopyTo(header, 0);

            WriteOctal(header, 100, 8, 0b110_100_100);
            WriteOctal(header, 108, 8, 0);
            WriteOctal(header, 116, 8, 0);
            WriteSize(header, size);
            WriteOctal(header, 136, 12, Math.Max(0, modified.ToUnixTimeSeconds()));
            header[156] = (byte)'0';
            Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
            Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

            // The checksum is computed with its own field taken as spaces, then written as six octal digits,
            // a NUL and a space.
            for (var i = 148; i < 156; i++)
                header[i] = (byte)' ';
            var sum = header.Sum(b => (long)b);
            Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);

            stream.Write(header);
        }

        /// <summary>Zeros up to the next block after a file's <paramref name="size"/> bytes.</summary>
        public static void WritePadding(Stream stream, long size)
        {
            var padding = (int)(RoundUp(size) - size);
            if (padding > 0)
                stream.Write(new byte[padding]);
        }

        /// <summary>Two empty blocks: the end of the archive.</summary>
        public static void WriteEnd(Stream stream) => stream.Write(new byte[Block * 2]);

        private static void WriteSize(byte[] header, long size)
        {
            if (size < 8L * 1024 * 1024 * 1024)
            {
                WriteOctal(header, 124, 12, size);
                return;
            }

            // GNU base-256: the top bit of the first byte set, the number big-endian in the rest.
            header[124] = 0x80;
            for (var i = 0; i < 8; i++)
                header[124 + 11 - i] = (byte)(size >> (8 * i));
        }

        private static void WriteOctal(byte[] header, int offset, int length, long value) =>
            Encoding.ASCII.GetBytes(Convert.ToString(value, 8).PadLeft(length - 1, '0') + "\0").CopyTo(header, offset);

        internal static long ParseNumber(ReadOnlySpan<byte> field)
        {
            if ((field[0] & 0x80) != 0)
            {
                long value = field[0] & 0x7F;
                for (var i = 1; i < field.Length; i++)
                    value = (value << 8) | field[i];
                return value;
            }

            long octal = 0;
            foreach (var b in field)
            {
                if (b is (byte)' ' && octal == 0)
                    continue;
                if (b is < (byte)'0' or > (byte)'7')
                    break;
                octal = octal * 8 + (b - '0');
            }
            return octal;
        }

        private static bool ChecksumMatches(byte[] header)
        {
            var stored = ParseNumber(header.AsSpan(148, 8));
            long sum = 0;
            for (var i = 0; i < Block; i++)
                sum += i is >= 148 and < 156 ? ' ' : header[i];
            return stored == sum;
        }

        private static string HeaderName(byte[] header)
        {
            static string Field(byte[] h, int offset, int length)
            {
                var end = Array.IndexOf(h, (byte)0, offset, length);
                return Encoding.UTF8.GetString(h, offset, (end < 0 ? offset + length : end) - offset);
            }

            var name = Field(header, 0, 100);
            // POSIX ustar splits long names: a prefix of up to 155 bytes, then the name. GNU's "ustar  "
            // header keeps other fields there.
            var prefix = Encoding.ASCII.GetString(header, 257, 6) == "ustar\0" ? Field(header, 345, 155) : string.Empty;
            return prefix.Length > 0 ? prefix + "/" + name : name;
        }

        private static IEnumerable<(string Key, string Value)> ParsePax(string text)
        {
            foreach (var record in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = record.IndexOf(' ');
                var equals = record.IndexOf('=', Math.Max(0, space));
                if (space > 0 && equals > space)
                    yield return (record[(space + 1)..equals], record[(equals + 1)..]);
            }
        }

        private static string ReadText(Stream stream, long size)
        {
            if (size > 1024 * 1024)
                throw new FormatException(PlatformText.ImportTarHeaderTooLarge);
            var data = new byte[size];
            ReadFully(stream, data);
            return Encoding.UTF8.GetString(data);
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }

        private static long RoundUp(long size) => (size + Block - 1) / Block * Block;
    }
}
