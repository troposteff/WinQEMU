using System.Runtime.InteropServices;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// The settings of WinQEMU installed from GitHub, taken over at the first start of another installation (the
    /// Microsoft Store one).
    /// </summary>
    /// <remarks>
    /// Windows sees another package identity as another application: its own storage, so an empty settings file, and
    /// WinQEMU would show none of the user's machines (the library folder, its tree, the virtual networks are all in
    /// settings.json). The machines themselves are in their folders, and the remembered disk passwords in the user's
    /// Credential Manager: both are found again as they are. The GitHub installation's file is copied, never moved: it
    /// keeps working until the user uninstalls it.
    /// </remarks>
    public static class SettingsImport
    {
        /// <summary>WinQEMU signed by its author (CN=troposteff), installed from the GitHub releases.</summary>
        public const string SideloadedFamilyName = "cfde84db-a7f2-4241-ae31-4fd9bc34967b_6swaqgnpzyjaw";

        /// <summary>Whether this start took the settings over: the shell says so once.</summary>
        public static bool ImportedAtThisStart { get; private set; }

        /// <summary>Copies the GitHub installation's settings when this installation has none yet.</summary>
        public static void ImportIfFirstStart()
        {
            try
            {
                if (Import(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CurrentFamilyName(),
                        AppSettings.SettingsPath))
                    ImportedAtThisStart = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not taken over: WinQEMU starts with defaults, and the library folder can be chosen again.
            }
        }

        /// <summary>
        /// Copies <c>settings.json</c> of the GitHub installation to <paramref name="target"/>; false when there is nothing
        /// to do: this is that installation, or not a packaged one (<paramref name="currentFamily"/> null), or settings
        /// exist already, or the GitHub installation has none.
        /// </summary>
        public static bool Import(string localAppData, string? currentFamily, string target)
        {
            if (currentFamily is null || string.Equals(currentFamily, SideloadedFamilyName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (File.Exists(target))
                return false;
            // The package's own view of %LOCALAPPDATA%\WinQEMU: where AppSettings writes, redirected by Windows.
            var source = Path.Combine(localAppData, "Packages", SideloadedFamilyName, "LocalCache", "Local", "WinQEMU", "settings.json");
            if (!File.Exists(source))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            return true;
        }

        /// <summary>This process's package family; null outside a package.</summary>
        private static string? CurrentFamilyName()
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer)
                return null;
            var name = new char[length];
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? new string(name, 0, (int)length - 1) : null;
        }

        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref uint length, char[]? name);
    }
}
