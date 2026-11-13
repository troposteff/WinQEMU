using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// User-level application settings, kept separate from VM descriptors so a VM folder stays portable.
    /// </summary>
    public sealed class AppSettings : ObservableBase
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private string _qemuDirectory = string.Empty;
        /// <summary>
        /// Explicit QEMU install directory. When set, auto-detection is skipped entirely so the
        /// user's choice is never silently overridden by another install on the machine.
        /// </summary>
        public string QemuDirectory { get => _qemuDirectory; set => SetProperty(ref _qemuDirectory, value); }

        private string _libraryRoot = string.Empty;
        /// <summary>Folder holding one subfolder per VM. Empty means <see cref="DefaultLibraryRoot"/>.</summary>
        public string LibraryRoot { get => _libraryRoot; set => SetProperty(ref _libraryRoot, value); }

        /// <summary>
        /// The virtual networks machines' cards plug into. Here rather than with the machines: a network
        /// is shared by several of them and outlives each, as in VMware's network editor.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<Models.VirtualNetwork> VirtualNetworks { get; set; } = new();

        private string _audioOutputId = string.Empty;
        /// <summary>
        /// The output every machine's sound goes to, by Windows endpoint id; empty for Windows' default one.
        /// One for all machines, because Windows keeps it per program, and they are all qemu-system-x86_64.exe
        /// (see Interop.HostAudio.RouteProgramOutput).
        /// </summary>
        public string AudioOutputId { get => _audioOutputId; set => SetProperty(ref _audioOutputId, value ?? string.Empty); }

        private string _virtioWinIsoPath = string.Empty;
        /// <summary>virtio-win's ISO, once chosen to install a Windows guest's tools (see GuestTools.FindVirtioWinIso).</summary>
        public string VirtioWinIsoPath { get => _virtioWinIsoPath; set => SetProperty(ref _virtioWinIsoPath, value ?? string.Empty); }

        private string _theme = string.Empty;
        /// <summary>The window's theme: "light", "dark", or empty to follow Windows. Applied at once.</summary>
        public string Theme { get => _theme; set => SetProperty(ref _theme, value ?? string.Empty); }

        private string _language = string.Empty;
        /// <summary>
        /// The interface's language: "en", "fr", or empty for Windows' own (see Text.UiLanguage). Applied at once.
        /// </summary>
        public string Language { get => _language; set => SetProperty(ref _language, value ?? string.Empty); }

        private bool _readinessDismissed;
        /// <summary>
        /// "Do not show again" ticked in the readiness assistant: it no longer opens by itself at start while this PC is not
        /// ready to run machines. It stays in the settings (Vérifier ce PC).
        /// </summary>
        public bool ReadinessDismissed { get => _readinessDismissed; set => SetProperty(ref _readinessDismissed, value); }

        private Models.LibraryTree _library = new();
        /// <summary>
        /// The library's folders and which machine is in which, as VMware's inventory: the application's view
        /// of its machines, so kept here and not in their descriptors. Absent from older files: an empty tree.
        /// </summary>
        public Models.LibraryTree Library { get => _library; set => _library = value ?? new(); }

        /// <summary>
        /// Where the window was when WinQEMU closed, so that it opens there again, on the same screen. Null until then
        /// (and in older files): Windows places it.
        /// </summary>
        public WindowPlace? Window { get; set; }

        [JsonIgnore]
        public string EffectiveLibraryRoot =>
            string.IsNullOrWhiteSpace(LibraryRoot) ? DefaultLibraryRoot : LibraryRoot;

        public static string DefaultLibraryRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WinQEMU VMs");

        public static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinQEMU");

        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

        /// <param name="importFirst">
        /// False for the winqemu command, which only wants the language: taking the GitHub installation's settings over is
        /// WinQEMU's to do, at its own first start, which then asks to uninstall that installation. Done by a command typed
        /// first, the window found settings already there and never asked.
        /// </param>
        public static AppSettings Load(bool importFirst = true)
        {
            if (importFirst)
                SettingsImport.ImportIfFirstStart();
            if (SafeFile.ReadValid(SettingsPath, text => Parse(text) is not null) is { } json)
                return Parse(json)!;

            // Neither the file nor its backup reads: defaults, so that WinQEMU starts. The unreadable file is put aside
            // first, never saved over (the next save would have replaced the library root, networks and folders for good).
            if (File.Exists(SettingsPath))
            {
                try
                {
                    File.Move(SettingsPath, SettingsPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".unreadable");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held: nothing is lost as long as it is not saved over, which Save then refuses.
                    _unreadableKept = true;
                }
            }
            return new AppSettings();
        }

        private static AppSettings? Parse(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool _unreadableKept;

        public void Save()
        {
            if (_unreadableKept && File.Exists(SettingsPath))
                return;
            _unreadableKept = false;
            Directory.CreateDirectory(SettingsDirectory);
            SafeFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
        }
    }

    /// <summary>
    /// The window's restored rectangle, in Windows' workspace coordinates and physical pixels (GetWindowPlacement's
    /// rcNormalPosition: the screen is the one that rectangle is on), and whether it was maximized over it.
    /// </summary>
    public sealed record WindowPlace(int Left, int Top, int Right, int Bottom, bool Maximized);
}
