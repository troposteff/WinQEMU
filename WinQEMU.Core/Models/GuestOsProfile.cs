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
        string Caveats = "",
        string? DownloadPage = null)
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
                Windows11Caveat + " " + VirtioCaveat, "https://www.microsoft.com/software-download/windows11"),

            new GuestOsProfile("windows-10", "Windows 10", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 64 * Gb, DiskBus.Sata, NicModel.E1000e, true, true,
                VirtioCaveat, "https://www.microsoft.com/software-download/windows10"),

            new GuestOsProfile("windows-server", "Windows Server 2019 / 2022 / 2025", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                4, 8192, 80 * Gb, DiskBus.Sata, NicModel.E1000e, true, true,
                VirtioCaveat, "https://www.microsoft.com/evalcenter"),

            new GuestOsProfile("windows-7", "Windows 7 / 8.1", "Windows",
                GuestArchitecture.X86_64, FirmwareType.Bios,
                2, 2048, 40 * Gb, DiskBus.Ide, NicModel.Rtl8139, true, true,
                CoreText.GuestOsWindows7Caveat),

            Linux("ubuntu", "Ubuntu", 2, 4096, 32, "https://ubuntu.com/download/desktop"),
            Linux("debian", "Debian", 2, 4096, 32, "https://www.debian.org/download"),

            Linux("linux-mint", "Linux Mint", 2, 4096, 32, "https://www.linuxmint.com/download.php"),
            Linux("pop-os", "Pop!_OS", 2, 4096, 32, "https://system76.com/pop/download/"),
            Linux("zorin", "Zorin OS", 2, 4096, 32, "https://zorin.com/os/download/"),
            Linux("kali", "Kali Linux", 2, 4096, 40, "https://www.kali.org/get-kali/"),

            // Fedora was "Fedora / RHEL / Rocky": the enterprise family has a profile of its own, and machines created
            // under the old one keep it (the key did not change).
            Linux("fedora", "Fedora", 2, 4096, 32, "https://fedoraproject.org/"),
            Linux("rhel", "RHEL / Rocky Linux / AlmaLinux / CentOS Stream", 2, 4096, 32, "https://rockylinux.org/download"),
            Linux("opensuse", "openSUSE / SUSE Linux Enterprise", 2, 4096, 32, "https://get.opensuse.org/"),
            Linux("arch", "Arch Linux", 2, 2048, 20, "https://archlinux.org/download/"),
            Linux("manjaro", "Manjaro", 2, 4096, 32, "https://manjaro.org/products/download/x86"),
            Linux("nixos", "NixOS", 2, 4096, 32, "https://nixos.org/download/"),
            Linux("alpine", "Alpine Linux", 1, 1024, 8, "https://alpinelinux.org/downloads/"),

            new GuestOsProfile("linux", CoreText.GuestOsLinuxGeneric, "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("linux-arm64", "Linux ARM64", "Linux",
                GuestArchitecture.Aarch64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false,
                CoreText.GuestOsArm64Caveat, "https://ubuntu.com/download/server/arm"),

            // FreeBSD and the firewalls built on it have virtio drivers in their kernel (vtblk, vtnet). "BSD" rather
            // than "Linux": the guest tools, shared folders and unattended installation are Linux and Windows only.
            Bsd("freebsd", "FreeBSD", 2, 2048, 20, "https://www.freebsd.org/where/"),
            Bsd("pfsense", "pfSense", 2, 2048, 16, "https://www.pfsense.org/download/", CoreText.GuestOsFirewallCaveat),
            Bsd("opnsense", "OPNsense", 2, 4096, 20, "https://opnsense.org/download/", CoreText.GuestOsFirewallCaveat),

            new GuestOsProfile("other", CoreText.GuestOsOther, "Other",
                GuestArchitecture.X86_64, FirmwareType.Bios,
                2, 2048, 20 * Gb, DiskBus.Sata, NicModel.E1000, false, false,
                CoreText.GuestOsOtherCaveat),
        };

        /// <summary>A 64-bit PC Linux: UEFI, virtio disk and card, UTC clock — what every current distribution boots.</summary>
        private static GuestOsProfile Linux(string key, string name, int cpus, int memoryMb, int diskGb, string page) =>
            new(key, name, "Linux", GuestArchitecture.X86_64, FirmwareType.Uefi, cpus, memoryMb, diskGb * Gb,
                DiskBus.VirtioBlk, NicModel.VirtioNet, false, false, DownloadPage: page);

        private static GuestOsProfile Bsd(string key, string name, int cpus, int memoryMb, int diskGb, string page,
            string caveats = "") =>
            new(key, name, "BSD", GuestArchitecture.X86_64, FirmwareType.Uefi, cpus, memoryMb, diskGb * Gb,
                DiskBus.VirtioBlk, NicModel.VirtioNet, false, false, caveats, page);

        public static GuestOsProfile Default => Profiles.First(p => p.Key == "linux");

        /// <summary>The families in catalog order (Windows, Linux, BSD, Other): the wizard's first choice, as in VMware's.</summary>
        public static IReadOnlyList<string> Families { get; } = Profiles.Select(p => p.Family).Distinct().ToList();

        public static GuestOsProfile? Find(string key) =>
            Profiles.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
