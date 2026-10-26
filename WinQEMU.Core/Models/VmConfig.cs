using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace WinQEMU.Core.Models
{
    /// <summary>
    /// Persisted description of a virtual machine. This is the single source of truth the
    /// argument builder translates into a QEMU command line; nothing else should invent flags.
    /// </summary>
    public class VmConfig : ObservableBase
    {
        public const string FileExtension = ".wqvm";

        private Guid _id = Guid.NewGuid();
        public Guid Id { get => _id; set => SetProperty(ref _id, value); }

        private string _name = "New Virtual Machine";
        public string Name { get => _name; set => SetProperty(ref _name, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => SetProperty(ref _description, value); }

        /// <summary>Free-form guest OS key (for example "windows11", "ubuntu"). Drives icon and default tuning.</summary>
        private string _guestOs = string.Empty;
        public string GuestOs { get => _guestOs; set => SetProperty(ref _guestOs, value); }

        // ---- Platform ----

        private GuestArchitecture _architecture = GuestArchitecture.X86_64;
        public GuestArchitecture Architecture { get => _architecture; set => SetProperty(ref _architecture, value); }

        private FirmwareType _firmware = FirmwareType.Uefi;
        public FirmwareType Firmware { get => _firmware; set => SetProperty(ref _firmware, value); }

        private AcceleratorType _accelerator = AcceleratorType.Auto;
        public AcceleratorType Accelerator { get => _accelerator; set => SetProperty(ref _accelerator, value); }

        private string _machineType = "q35";
        public string MachineType { get => _machineType; set => SetProperty(ref _machineType, value); }

        // ---- CPU / memory ----

        /// <summary>
        /// QEMU CPU model. Deliberately not <c>max</c>: under WHPX, <c>max</c> exposes features the
        /// hypervisor cannot honour (QEMU warns about APX/MPX conflicts) and the guest never executes
        /// — the firmware itself never starts and the console shows only "Display output is not
        /// active", with nothing in the log to explain it.
        /// A vendor-neutral base plus the instruction sets modern operating systems actually require
        /// starts reliably and is not tied to Intel or AMD.
        /// </summary>
        private string _cpuModel = "qemu64,+sse4.1,+sse4.2,+popcnt,+aes,+ssse3,+xsave";
        public string CpuModel { get => _cpuModel; set => SetProperty(ref _cpuModel, value); }

        private int _cpuCount = 2;
        public int CpuCount { get => _cpuCount; set => SetProperty(ref _cpuCount, value); }

        private int _coresPerSocket = 1;
        /// <summary>Cores per socket. 1 means let QEMU derive the topology from <see cref="CpuCount"/>.</summary>
        public int CoresPerSocket { get => _coresPerSocket; set => SetProperty(ref _coresPerSocket, value); }

        private int _memoryMb = 4096;
        public int MemoryMb { get => _memoryMb; set => SetProperty(ref _memoryMb, value); }

        private bool _enableBalloon = true;
        public bool EnableBalloon { get => _enableBalloon; set => SetProperty(ref _enableBalloon, value); }

        // ---- Storage ----

        public ObservableCollection<DiskConfig> Disks { get; set; } = new();

        private string _isoPath = string.Empty;
        public string IsoPath { get => _isoPath; set => SetProperty(ref _isoPath, value); }

        private string _secondaryIsoPath = string.Empty;
        /// <summary>Second optical drive, typically the virtio-win driver ISO during Windows setup.</summary>
        public string SecondaryIsoPath { get => _secondaryIsoPath; set => SetProperty(ref _secondaryIsoPath, value); }

        private bool _bootFromIso;
        public bool BootFromIso { get => _bootFromIso; set => SetProperty(ref _bootFromIso, value); }

        /// <summary>
        /// Device ids in boot order, most preferred first — disk ids plus <c>cd0</c>/<c>cd1</c> for the
        /// optical drives. Authoritative when non-empty; left empty by the creation wizard, which
        /// expresses its simpler intent through <see cref="BootFromIso"/> alone. Descriptors written
        /// before this field existed therefore keep booting exactly as they did.
        /// </summary>
        public ObservableCollection<string> BootOrder { get; set; } = new();

        // ---- Devices ----

        public ObservableCollection<NetworkAdapterConfig> NetworkAdapters { get; set; } = new();

        private GraphicsAdapter _graphics = GraphicsAdapter.VirtioVga;
        public GraphicsAdapter Graphics { get => _graphics; set => SetProperty(ref _graphics, value); }

        private int _videoMemoryMb = 64;
        public int VideoMemoryMb { get => _videoMemoryMb; set => SetProperty(ref _videoMemoryMb, value); }

        private bool _enableAudio = true;
        public bool EnableAudio { get => _enableAudio; set => SetProperty(ref _enableAudio, value); }

        private bool _enableUsbTablet = true;
        /// <summary>Absolute pointing device. Without it the guest cursor drifts from the host cursor.</summary>
        public bool EnableUsbTablet { get => _enableUsbTablet; set => SetProperty(ref _enableUsbTablet, value); }

        /// <summary>Host USB devices connected at every start (see <see cref="UsbDeviceConfig"/>).</summary>
        public ObservableCollection<UsbDeviceConfig> UsbDevices { get; set; } = new();

        private bool _enableClipboard = true;
        /// <summary>
        /// Shares the text clipboard with the guest. Needs spice-vdagent inside the guest (installed by
        /// default on Ubuntu and Fedora desktops; the SPICE guest tools on Windows).
        /// </summary>
        public bool EnableClipboard { get => _enableClipboard; set => SetProperty(ref _enableClipboard, value); }

        private bool _rtcLocalTime = true;
        /// <summary>Windows guests expect the RTC in local time; most Linux guests expect UTC.</summary>
        public bool RtcLocalTime { get => _rtcLocalTime; set => SetProperty(ref _rtcLocalTime, value); }

        // ---- Escape hatch ----

        private string _extraArguments = string.Empty;
        /// <summary>
        /// Raw arguments appended verbatim to the command line. Deliberate escape hatch so an
        /// unsupported QEMU feature never blocks the user.
        /// </summary>
        public string ExtraArguments { get => _extraArguments; set => SetProperty(ref _extraArguments, value); }

        // ---- Bookkeeping ----

        private DateTime _createdUtc = DateTime.UtcNow;
        public DateTime CreatedUtc { get => _createdUtc; set => SetProperty(ref _createdUtc, value); }

        private DateTime? _lastStartedUtc;
        public DateTime? LastStartedUtc { get => _lastStartedUtc; set => SetProperty(ref _lastStartedUtc, value); }

        /// <summary>
        /// Absolute path of the folder holding this VM's descriptor and disks. Assigned by the
        /// library on load, never serialised, so moving a folder just works.
        /// </summary>
        [JsonIgnore]
        public string FolderPath { get; set; } = string.Empty;

        /// <summary>
        /// Path of this machine's descriptor. The name is trimmed: Windows silently drops a trailing
        /// space from a file name, so "Fedora " would be written to Fedora.wqvm and then not be
        /// recognised as its own descriptor on the next save, which deleted it as a stale one.
        /// </summary>
        [JsonIgnore]
        public string DescriptorPath => Path.Combine(FolderPath, Name.Trim() + FileExtension);

        /// <summary>Resolves a possibly-relative disk path against <see cref="FolderPath"/>.</summary>
        public string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(FolderPath, path));
        }
    }
}
