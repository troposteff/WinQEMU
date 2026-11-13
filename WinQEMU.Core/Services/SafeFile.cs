using System.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Writes a settings file or a machine descriptor whole or not at all, keeping the previous version beside it.
    /// </summary>
    /// <remarks>
    /// File.WriteAllText empties the file first: a power cut or a crash while writing left an empty or zero-filled
    /// file. The descriptor's machine then vanished from the library (and from its folder in the tree), and an
    /// unreadable settings.json was read as defaults and saved over at the next save, the library root, virtual
    /// networks and folders with it. The text goes to a temporary file, flushed to the disk, which then replaces the
    /// target (File.Replace) and leaves the previous one as <c>.bak</c>, read when the file itself cannot be.
    /// </remarks>
    public static class SafeFile
    {
        public static string BackupOf(string path) => path + ".bak";

        public static void WriteAllText(string path, string text)
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, BackupOf(path), ignoreMetadataErrors: true);
            else
                File.Move(temporary, path);
        }

        /// <summary>
        /// The file's text when <paramref name="isValid"/> accepts it, else its backup's; null when neither can be read.
        /// A file held a moment by another program (a backup, an antivirus) is tried again before giving up.
        /// </summary>
        public static string? ReadValid(string path, Func<string, bool> isValid)
        {
            foreach (var candidate in new[] { path, BackupOf(path) })
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (!File.Exists(candidate))
                            break;
                        var text = File.ReadAllText(candidate);
                        if (isValid(text))
                            return text;
                        break;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        Thread.Sleep(200);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }
                }
            }
            return null;
        }
    }
}
