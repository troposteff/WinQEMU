using System.Text.Json.Serialization;

namespace WinQEMU.Core.Models
{
    /// <summary>
    /// A host folder served to the guest, which finds it as ~/Partages/<see cref="GuestName"/> (see
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

        [JsonIgnore]
        public string GuestPath => $"~/Partages/{GuestName}";

        [JsonIgnore]
        public string AccessText => ReadOnly ? "Lecture seule" : "Lecture et écriture";
    }
}
