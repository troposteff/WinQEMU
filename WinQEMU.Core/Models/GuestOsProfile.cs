using WinQEMU.Core.Text;

namespace WinQEMU.Core.Models
{
    /// <summary>
    /// Sensible defaults for a guest operating system, plus the caveats that come with it.
    /// </summary>
    /// <param name="Key">Stable identifier stored in <see cref="VmConfig.GuestOs"/>.</param>
    /// <param name="DisplayName">Name shown in the creation wizard.</param>
    /// <param name="Family">Grouping header, e.g. "Windows" or "Linux".</param>
    /// <param name="DiskBus">
    /// Chosen for what the installer can actually see. Windows has no inbox virtio-blk driver, so a
    /// virtio system disk makes setup report "no drive found" unless the driver ISO is supplied —
    /// these profiles default to SATA and mention virtio as an opt-in.
    /// </param>
    /// <param name="RtcLocalTime">Windows expects the hardware clock in local time; Unix expects UTC.</param>
    /// <param name="SuggestsVirtioDrivers">
    /// True when the guest would benefit from the virtio-win driver ISO in a second optical drive.
    /// </param>
    /// <param name="Caveats">
    /// Things that will bite the user during installation. Shown in the wizard rather than discovered
    /// halfway through a failed setup.
    /// </param>
    public sealed record GuestOsProfile(
        string Key,
        string DisplayName,
        string Family,
        GuestArchitecture Architecture,
        FirmwareType Firmware,
        int CpuCount,
        int MemoryMb,
        long DiskCapacityBytes,
        DiskBus DiskBus,
        NicModel NicModel,
        bool RtcLocalTime,
        bool SuggestsVirtioDrivers,
        string Caveats = "")
    {
        public double DiskCapacityGb => DiskCapacityBytes / 1024d / 1024d / 1024d;
    }

    public static class GuestOsCatalog
    {
        private const long Gb = 1024L * 1024 * 1024;

        /// <summary>
        /// Windows 11 refuses to install without a TPM 2.0 and Secure Boot. QEMU can provide Secure
        /// Boot through the edk2 firmware, but a virtual TPM needs an external swtpm daemon that the
        /// Windows QEMU builds do not ship, so this is stated up front rather than left to fail.
        /// </summary>
        private static string Windows11Caveat => CoreText.GuestOsWindows11Caveat;

        private static string VirtioCaveat => CoreText.GuestOsVirtioCaveat;

        public static IReadOnlyList<GuestOsProfile> Profiles { get; } = new[]
        {
            new GuestOsProfile("windows-11", "Windows 11", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                4, 8192, 64 * Gb, DiskBus.Sata, NicModel.E1000e, true, true,
                Windows11Caveat + " " + VirtioCaveat),

            new GuestOsProfile("windows-10", "Windows 10", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 64 * Gb, DiskBus.Sata, NicModel.E1000e, true, true,
                VirtioCaveat),

            new GuestOsProfile("windows-server", "Windows Server 2019 / 2022", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                4, 8192, 80 * Gb, DiskBus.Sata, NicModel.E1000e, true, true,
                VirtioCaveat),

            new GuestOsProfile("windows-7", "Windows 7 / 8.1", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Bios,
                2, 2048, 40 * Gb, DiskBus.Ide, NicModel.Rtl8139, true, true,
                CoreText.GuestOsWindows7Caveat),

            new GuestOsProfile("ubuntu", "Ubuntu", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("debian", "Debian", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("fedora", "Fedora / RHEL / Rocky", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("linux", CoreText.GuestOsLinuxGeneric, "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("linux-arm64", "Linux ARM64", "Linux",
                GuestArchitecture.Aarch64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false,
                CoreText.GuestOsArm64Caveat),

            new GuestOsProfile("other", CoreText.GuestOsOther, "Autre",
                GuestArchitecture.X86_64, FirmwareType.Bios,
                2, 2048, 20 * Gb, DiskBus.Sata, NicModel.E1000, false, false,
                CoreText.GuestOsOtherCaveat),
        };

        public static GuestOsProfile Default => Profiles.First(p => p.Key == "linux");

        public static GuestOsProfile? Find(string key) =>
            Profiles.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
