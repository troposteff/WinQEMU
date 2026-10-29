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
        /// The interface's language: "en", "fr", or empty for Windows' own (see Text.UiLanguage). Read once at
        /// start: a change takes effect the next time WinQEMU starts.
        /// </summary>
        public string Language { get => _language; set => SetProperty(ref _language, value ?? string.Empty); }

        private Models.LibraryTree _library = new();
        /// <summary>
        /// The library's folders and which machine is in which, as VMware's inventory: the application's view
        /// of its machines, so kept here and not in their descriptors. Absent from older files: an empty tree.
        /// </summary>
        public Models.LibraryTree Library { get => _library; set => _library = value ?? new(); }

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

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                    if (settings is not null)
                        return settings;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt or unreadable settings file must not prevent the app from starting;
                // defaults plus auto-detection are a usable fallback.
            }

            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
        }
    }
}
