using WinQEMU.Core.Models;

namespace WinQEMU.Core.Import
{
    public enum ImportSource
    {
        VMware,
        VirtualBox,
        /// <summary>An OVF package (.ova, or .ovf with its files), as VMware, VirtualBox and Proxmox export.</summary>
        Ovf,
        /// <summary>A lone disk image: what Hyper-V leaves readable, or any image without its machine.</summary>
        DiskImage
    }

    /// <summary>A disk of the machine to import, as found in its configuration.</summary>
    public sealed class ImportedDisk
    {
        /// <summary>Absolute path of the image the source machine writes to — the top of its chain.</summary>
        public required string SourcePath { get; init; }

        /// <summary>
        /// What qemu-img opens when it is not the file itself: a disk inside an .ova, read in place as a
        /// <c>json:</c> filename (format driver over raw's offset and size), so nothing is extracted.
        /// </summary>
        public string? QemuSource { get; init; }

        /// <summary>The disk's file name inside its .ova, for display.</summary>
        public string? PackageEntry { get; init; }

        /// <summary>The image as qemu-img takes it.</summary>
        public string ImageSpec => QemuSource ?? SourcePath;

        /// <summary>The controller to attach it to here, chosen for what the guest can already boot from.</summary>
        public DiskBus Bus { get; set; }

        /// <summary>How the source described its controller, e.g. "scsi0:0 (lsisas1068)", for the summary.</summary>
        public string SourceSlot { get; init; } = string.Empty;

        /// <summary>Read from the image by <see cref="VmImporter"/>; 0 until then.</summary>
        public long CapacityBytes { get; set; }

        public string Format { get; set; } = string.Empty;
    }

    public sealed class ImportedAdapter
    {
        public NicModel Model { get; set; }

        /// <summary>The source's address, kept: guests tie their network configuration to it.</summary>
        public string MacAddress { get; init; } = string.Empty;

        public NetworkMode Mode { get; init; } = NetworkMode.Nat;
    }

    /// <summary>
    /// What a foreign machine becomes here, read from its configuration before anything is copied, so it
    /// can be shown and confirmed. Hardware the source configuration does not give keeps the defaults of
    /// the guest profile.
    /// </summary>
    public sealed class ImportPlan
    {
        public required ImportSource Source { get; init; }

        /// <summary>The file the user chose: .vmx, .vbox or the disk image itself.</summary>
        public required string SourcePath { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>A <see cref="GuestOsCatalog"/> key.</summary>
        public string GuestOs { get; set; } = "other";

        /// <summary>The source's own name for the guest system, shown next to the profile it maps to.</summary>
        public string SourceGuestOs { get; set; } = string.Empty;

        public FirmwareType Firmware { get; set; } = FirmwareType.Bios;
        public int CpuCount { get; set; } = 2;
        public int CoresPerSocket { get; set; } = 1;
        public int MemoryMb { get; set; } = 2048;
        public string IsoPath { get; set; } = string.Empty;

        public List<ImportedDisk> Disks { get; } = new();
        public List<ImportedAdapter> Adapters { get; } = new();

        /// <summary>What will not come across as it was, phrased for display.</summary>
        public List<string> Notes { get; } = new();

        /// <summary>Why the machine cannot be imported as it is, or null. Set while reading, never thrown.</summary>
        public string? Blocker { get; set; }

        public GuestOsProfile Profile => GuestOsCatalog.Find(GuestOs) ?? GuestOsCatalog.Profiles.Last();

        public bool IsWindows => Profile.Family == "Windows";
    }
}
