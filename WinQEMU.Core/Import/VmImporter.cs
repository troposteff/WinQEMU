using System.Text;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Import
{
    /// <summary>
    /// Turns the file the user picked into an <see cref="ImportPlan"/>: the machine's configuration when
    /// there is one, then each disk measured with qemu-img, which also proves it can be read at all.
    /// </summary>
    public static class VmImporter
    {
        /// <summary>The extensions offered by the file picker, configuration files first.</summary>
        public static IReadOnlyList<string> Extensions { get; } = new[] { ".vmx", ".vbox", ".ova", ".ovf", ".vhdx", ".vhd", ".avhdx", ".vmdk", ".vdi", ".qcow2", ".img" };

        public static async Task<ImportPlan> ReadAsync(string path, QemuImgService images, CancellationToken cancellationToken = default)
        {
            var full = Path.GetFullPath(path);
            var plan = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".vmx" => VmxReader.Read(full),
                ".vbox" => VboxReader.Read(full),
                ".ova" or ".ovf" => OvfReader.Read(full),
                _ => DiskOnly(full)
            };

            if (plan.Blocker is null && plan.Disks.Count == 0)
                plan.Blocker = "Aucun disque n'a été trouvé dans cette configuration.";

            foreach (var disk in plan.Disks.Where(d => File.Exists(d.SourcePath)))
            {
                try
                {
                    var info = await images.GetInfoAsync(disk.ImageSpec, cancellationToken).ConfigureAwait(false);
                    disk.CapacityBytes = info.VirtualSizeBytes;
                    disk.Format = info.Format;
                }
                catch (QemuImgException ex)
                {
                    plan.Blocker ??= Unreadable(disk.SourcePath, ex);
                }
            }

            if (plan.Source == ImportSource.DiskImage && plan.Blocker is null)
                await InspectDiskAsync(plan, images, cancellationToken).ConfigureAwait(false);

            return plan;
        }

        /// <summary>
        /// A disk alone. Hyper-V keeps its machines in .vmcx files, a binary format with no published
        /// description, so its disk is all that can be read; the same goes for an image found on its own.
        /// The hardware is settled by <see cref="InspectDiskAsync"/>, once the partitions tell what the
        /// guest is.
        /// </summary>
        private static ImportPlan DiskOnly(string path)
        {
            var plan = new ImportPlan
            {
                Source = ImportSource.DiskImage,
                SourcePath = path,
                Name = Path.GetFileNameWithoutExtension(path)
            };

            plan.Disks.Add(new ImportedDisk { SourcePath = path, SourceSlot = Path.GetFileName(path) });
            plan.Adapters.Add(new ImportedAdapter { Mode = NetworkMode.Nat });
            UseProfile(plan, "other");

            if (Path.GetExtension(path).ToLowerInvariant() is ".avhdx" or ".avhd")
            {
                plan.Blocker = "C'est un disque de point de contrôle Hyper-V (différentiel), que QEMU ne sait pas lire. " +
                               "Supprimez les points de contrôle dans Hyper-V (leur contenu est fusionné dans le disque " +
                               "principal), puis importez le .vhdx.";
            }

            return plan;
        }

        /// <summary>
        /// Reads the partition table: UEFI when the disk carries a GPT, BIOS otherwise (booting a GPT system
        /// disk with a BIOS, or the reverse, ends on a firmware that finds nothing to start), and the guest
        /// family from the partition types, which decides the disk controller.
        /// </summary>
        private static async Task InspectDiskAsync(ImportPlan plan, QemuImgService images, CancellationToken cancellationToken)
        {
            var hyperV = Path.GetExtension(plan.SourcePath).ToLowerInvariant() is ".vhdx" or ".vhd";
            byte[] start;
            try
            {
                start = await images.ReadStartAsync(plan.Disks[0].ImageSpec, PartitionTableSectors, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is QemuImgException or IOException)
            {
                plan.Notes.Add("La table de partitions n'a pas pu être lue : la machine démarrera en BIOS sur un disque " +
                               "SATA. Ajustez le microprogramme, le contrôleur et le système invité dans Matériel.");
                return;
            }

            plan.Firmware = HasGpt(start) ? FirmwareType.Uefi : FirmwareType.Bios;
            var family = DetectFamily(start);
            UseProfile(plan, family switch
            {
                GuestFamily.Windows => "windows-10",
                GuestFamily.Linux => "linux",
                _ => "other"
            });

            plan.Notes.Add(family switch
            {
                GuestFamily.Windows => "Seul le disque est importé. Ses partitions sont celles d'un Windows : la machine " +
                                       "prend les réglages de Windows 10 (SATA, carte e1000e), à ajuster dans Matériel.",
                GuestFamily.Linux => "Seul le disque est importé. Ses partitions sont celles d'un Linux : la machine prend " +
                                     "les réglages Linux (disque et carte réseau VirtIO), à ajuster dans Matériel.",
                _ => "Seul le disque est importé, et ses partitions ne disent pas quel système il porte : la machine " +
                     "prend des réglages prudents (SATA, carte e1000), à ajuster dans Matériel avec le système invité."
            });

            if (hyperV && family != GuestFamily.Linux)
            {
                plan.Notes.Add("Un Windows venant de Hyper-V démarrait sur le contrôleur synthétique de Hyper-V, qui " +
                               "n'existe pas ici. S'il s'arrête sur INACCESSIBLE_BOOT_DEVICE, activez son pilote AHCI " +
                               "avant d'exporter (sc config storahci start= boot dans la machine Hyper-V).");
            }
            if (hyperV && family != GuestFamily.Windows)
            {
                // Seen with Ubuntu 26.04: dracut builds a host-only initramfs, so on Hyper-V it holds
                // hv_storvsc and no AHCI driver; on SATA the guest ends in emergency mode with "/dev/disk/
                // by-uuid/... does not exist". VirtIO works there because Ubuntu builds virtio_blk into the
                // kernel, but a distribution that ships it as a module fails the same way on any controller.
                plan.Notes.Add("Un Linux installé sous Hyper-V peut n'avoir dans son initramfs que les pilotes de disque " +
                               "de Hyper-V. S'il s'arrête en « emergency mode » en signalant que /dev/disk/by-uuid/… " +
                               "n'existe pas, régénérez-le avec tous les pilotes dans la machine Hyper-V, puis réimportez " +
                               "le disque : echo 'hostonly=\"no\"' | sudo tee /etc/dracut.conf.d/90-generic.conf, puis " +
                               "sudo dracut --force --regenerate-all (ou sudo update-initramfs -u -k all si la " +
                               "distribution n'utilise pas dracut).");
            }
        }

        /// <summary>The protective MBR, the GPT header and 128 entries of 128 bytes: what every GPT tool writes.</summary>
        private const int PartitionTableSectors = 34;

        /// <summary>CPU, memory, disk controller and network card of a profile, for a machine with no configuration.</summary>
        private static void UseProfile(ImportPlan plan, string guestOs)
        {
            plan.GuestOs = guestOs;
            var profile = plan.Profile;
            plan.CpuCount = profile.CpuCount;
            plan.MemoryMb = profile.MemoryMb;
            foreach (var disk in plan.Disks)
                disk.Bus = profile.DiskBus;
            foreach (var adapter in plan.Adapters)
                adapter.Model = profile.NicModel;
        }

        /// <summary>The "EFI PART" signature at the start of the second sector, where a GPT header lives.</summary>
        public static bool HasGpt(byte[] start) =>
            start.Length >= 520 && Encoding.ASCII.GetString(start, 512, 8) == "EFI PART";

        public enum GuestFamily { Unknown, Windows, Linux }

        // Partition type GUIDs. "Basic data" alone proves nothing (Linux data disks use it too); the
        // Microsoft reserved and recovery partitions only exist on Windows system disks.
        private static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
        private static readonly Guid WindowsRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
        private static readonly Guid[] LinuxTypes =
        {
            new("0FC63DAF-8483-4772-8E79-3D69D8477DE4"),   // Linux filesystem
            new("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709"),   // root (x86-64)
            new("E6D6D379-F507-44C2-A23C-238F2A3DF928"),   // LVM
            new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F"),   // swap
            new("A19D880F-05FC-4D3B-A006-743F0F84911E"),   // RAID
            new("BC13C2FF-59E6-4262-A352-B275FD6F7172")    // extended boot loader (/boot)
        };

        /// <summary>
        /// What the partition types say about the system on the disk: GPT type GUIDs, or MBR type bytes
        /// (0x83 Linux, 0x82 swap, 0x8E LVM; 0x07 NTFS, 0x27 Windows recovery).
        /// </summary>
        public static GuestFamily DetectFamily(byte[] start)
        {
            if (HasGpt(start))
            {
                var entriesLba = BitConverter.ToInt64(start, 512 + 72);
                var count = BitConverter.ToUInt32(start, 512 + 80);
                var size = BitConverter.ToUInt32(start, 512 + 84);
                if (entriesLba < 2 || size < 16)
                    return GuestFamily.Unknown;

                // GUIDs are stored in the same mixed-endian layout the Guid(bytes) constructor reads.
                var types = new List<Guid>();
                for (long i = 0; i < count; i++)
                {
                    var offset = entriesLba * 512 + i * size;
                    if (offset + 16 > start.Length)
                        break;
                    var type = new Guid(start.AsSpan((int)offset, 16));
                    if (type != Guid.Empty)
                        types.Add(type);
                }

                if (types.Contains(MicrosoftReserved) || types.Contains(WindowsRecovery))
                    return GuestFamily.Windows;
                return types.Any(t => LinuxTypes.Contains(t)) ? GuestFamily.Linux : GuestFamily.Unknown;
            }

            if (start.Length < 512 || start[510] != 0x55 || start[511] != 0xAA)
                return GuestFamily.Unknown;

            var mbrTypes = Enumerable.Range(0, 4).Select(i => start[446 + i * 16 + 4]).ToList();
            if (mbrTypes.Any(t => t is 0x83 or 0x82 or 0x8E))
                return GuestFamily.Linux;
            return mbrTypes.Any(t => t is 0x07 or 0x27) ? GuestFamily.Windows : GuestFamily.Unknown;
        }

        private static string Unreadable(string path, QemuImgException ex)
        {
            var reason = ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
                         || ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)
                ? "il est ouvert par un autre programme. Éteignez la machine source et fermez son hyperviseur."
                : ex.Message;
            return $"Le disque {Path.GetFileName(path)} ne peut pas être lu : {reason}";
        }
    }
}
