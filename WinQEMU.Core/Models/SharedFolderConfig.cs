using System.Text.Json.Serialization;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Models
{
    /// <summary>
    /// A host folder served to the guest, which finds it as ~/Shares/<see cref="GuestName"/> (~/Partages in French; see
    /// Sharing.NinePServer and Services.GuestShareMounter).
    /// </summary>
    public class SharedFolderConfig : ObservableBase
    {
        private string _hostPath = string.Empty;
        public string HostPath { get => _hostPath; set => SetProperty(ref _hostPath, value); }

        private string _name = string.Empty;
        /// <summary>Its directory name in the guest, unique within the machine.</summary>
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private bool _readOnly;
        public bool ReadOnly { get => _readOnly; set => SetProperty(ref _readOnly, value); }

        /// <summary>
        /// <see cref="Name"/>, or the folder's own name for a descriptor written by the version that shared
        /// folders through Windows (it recorded a share name instead, now ignored).
        /// </summary>
        [JsonIgnore]
        public string GuestName => Name.Length > 0 ? Name : DefaultNameFor(HostPath);

        /// <summary>The folder's own name; for a drive's root, its letter ("D:\" → "D").</summary>
        public static string DefaultNameFor(string hostPath)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(hostPath));
            return name.Length > 0 ? name : hostPath.TrimEnd('\\', ':');
        }

        private string _guestFolder = string.Empty;
        /// <summary>
        /// The folder the guest mounts its shares in, as it last reported it (VmConfig.GuestSharesFolder); empty
        /// until then. A guest set up while WinQEMU was in the other language keeps its own (see
        /// GuestShareMounter.MountScript): an English WinQEMU showed ~/Shares/Music for an Ubuntu that has ~/Partages.
        /// </summary>
        [JsonIgnore]
        public string GuestFolder
        {
            get => _guestFolder;
            set
            {
                if (SetProperty(ref _guestFolder, value ?? string.Empty))
                    RaisePropertyChanged(nameof(GuestPath));
            }
        }

        /// <summary>Where the guest finds it: in its own folder once known, else where a guest set up now would.</summary>
        [JsonIgnore]
        public string GuestPath => $"~/{(GuestFolder.Length > 0 ? GuestFolder : GuestShareMounter.GuestFolder)}/{GuestName}";

        [JsonIgnore]
        public string AccessText => ReadOnly ? CoreText.SharedFolderReadOnly : CoreText.SharedFolderReadWrite;
    }
}
