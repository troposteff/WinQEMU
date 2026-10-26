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
        private const string Windows11Caveat =
            "Windows 11 exige un TPM 2.0 et le démarrage sécurisé. Aucun des deux n'est possible ici : swtpm " +
            "est absent des versions Windows de QEMU, et le démarrage sécurisé réclame le mode SMM que " +
            "l'hyperviseur WHPX ne fournit pas. Le profil reste donc en UEFI simple ; l'installation exigera " +
            "de contourner le contrôle de configuration matérielle.";

        private const string VirtioCaveat =
            "Windows n'embarque pas de pilote virtio. Le disque est donc en SATA, qui fonctionne sans pilote. " +
            "Pour de meilleures performances, fournissez l'ISO virtio-win et repassez le disque en virtio " +
            "après l'installation.";

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
                "Ces versions ne gèrent pas l'UEFI de façon fiable sous QEMU : le microprogramme reste en BIOS " +
                "et le disque en IDE."),

            new GuestOsProfile("ubuntu", "Ubuntu", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("debian", "Debian", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("fedora", "Fedora / RHEL / Rocky", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("linux", "Linux (générique)", "Linux",
                GuestArchitecture.X86_64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false),

            new GuestOsProfile("linux-arm64", "Linux ARM64", "Linux",
                GuestArchitecture.Aarch64, FirmwareType.Uefi,
                2, 4096, 32 * Gb, DiskBus.VirtioBlk, NicModel.VirtioNet, false, false,
                "Architecture différente de l'hôte : QEMU émulera le processeur (TCG), sans accélération " +
                "matérielle. Comptez environ un ordre de grandeur de performances en moins."),

            new GuestOsProfile("other", "Autre / inconnu", "Autre",
                GuestArchitecture.X86_64, FirmwareType.Bios,
                2, 2048, 20 * Gb, DiskBus.Sata, NicModel.E1000, false, false,
                "Configuration prudente : BIOS et SATA, que pratiquement tout système sait démarrer."),
        };

        public static GuestOsProfile Default => Profiles.First(p => p.Key == "linux");

        public static GuestOsProfile? Find(string key) =>
            Profiles.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
