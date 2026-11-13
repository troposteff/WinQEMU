using System.Text.RegularExpressions;

namespace WinQEMU.Core.Sharing
{
    /// <summary>
    /// What a guest may reach in a shared folder, beyond names that stay names (each server checks those): never through
    /// a link, never a device.
    /// </summary>
    /// <remarks>
    /// The guest names paths; Windows follows the junctions and symbolic links it meets on them. Documents holds hidden
    /// junctions (My Music → ..\Music…), and a developer's folder links of its own: walking "My Music" from a shared
    /// Documents read and wrote the user's Music folder. Cloud placeholders (OneDrive's files on demand) are reparse
    /// points too, but not links (no target): they stay reachable, or sharing Documents in OneDrive would break.
    /// </remarks>
    public static class ShareGuard
    {
        // What Win32 turns into a device whatever the folder (C:\share\COM1 → \\.\COM1), with or without an extension,
        // superscript digits included.
        private static readonly Regex Device = new(
            @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Whether Windows reads the name as a device, wherever it is.</summary>
        public static bool IsDeviceName(string name) => Device.IsMatch(name.TrimEnd(' ', '.'));

        /// <summary>
        /// Whether <paramref name="relative"/>, under the shared folder <paramref name="root"/>, passes through or ends on a
        /// junction or symbolic link. What does not exist yet is no link (a file about to be created).
        /// </summary>
        public static bool CrossesLink(string root, string relative)
        {
            var current = root;
            foreach (var part in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (IsLink(current))
                    return true;
            }
            return false;
        }

        /// <summary>Whether the entry is a junction or a symbolic link (a reparse point with a target).</summary>
        public static bool IsLink(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    return false;
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                return info.LinkTarget is not null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: treated as a link, refused.
                return true;
            }
        }

        /// <summary>Whether a listed entry is one: <see cref="IsLink"/> from what the listing already read.</summary>
        public static bool IsLink(FileSystemInfo entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null;
    }
}
