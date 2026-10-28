using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Import
{
    /// <summary>
    /// Reads an OVF package: an .ova (a tar holding the .ovf descriptor first, then the disks) or an .ovf
    /// beside its files. The descriptor is DMTF's XML: a list of files, the disks made of them, and the
    /// hardware as CIM resource items (ResourceType 3 CPU, 4 memory, 5 IDE, 6 SCSI, 20 SATA/NVMe, 17 disk,
    /// 10 network card) linked by InstanceID and Parent.
    /// </summary>
    /// <remarks>
    /// Elements and attributes are matched by local name, as in <see cref="VboxReader"/>: OVF 1 and 2 use
    /// different namespaces, and VMware and VirtualBox add their own (vmw:osType, vbox:OSType).
    /// </remarks>
    public static class OvfReader
    {
        public static ImportPlan Read(string path)
        {
            var full = Path.GetFullPath(path);
            if (!Path.GetExtension(full).Equals(".ova", StringComparison.OrdinalIgnoreCase))
                return Parse(XDocument.Load(full), full, href => FileSource(full, href));

            var entries = TarArchive.ReadEntries(full);
            var descriptor = entries.FirstOrDefault(e => e.Name.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase))
                             ?? throw new FormatException("Ce paquet OVA ne contient aucun descripteur .ovf.");

            XDocument document;
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.Position = descriptor.DataOffset;
                var bytes = new byte[descriptor.Size];
                stream.ReadExactly(bytes);
                using var memory = new MemoryStream(bytes);
                document = XDocument.Load(memory);
            }

            return Parse(document, full, href =>
            {
                var entry = entries.FirstOrDefault(e => string.Equals(e.Name, href, StringComparison.OrdinalIgnoreCase))
                            ?? entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.Name), href, StringComparison.OrdinalIgnoreCase));
                return entry is null ? null : new DiskFile(full, entry);
            });
        }

        /// <summary>Where a referenced file lies: alone on disk, or inside the package.</summary>
        private sealed record DiskFile(string Path, TarEntry? Entry);

        private static DiskFile? FileSource(string ovfPath, string href)
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && !uri.IsFile)
                return null;
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ovfPath)!, href));
            return File.Exists(file) ? new DiskFile(file, null) : null;
        }

        private static ImportPlan Parse(XDocument document, string sourcePath, Func<string, DiskFile?> locate)
        {
            var root = document.Root ?? throw new FormatException("Le descripteur OVF est vide.");
            var plan = new ImportPlan { Source = ImportSource.Ovf, SourcePath = sourcePath };

            var systems = Descendants(root, "VirtualSystem").ToList();
            var system = systems.FirstOrDefault();
            if (system is null)
            {
                plan.Blocker = "Le descripteur OVF ne décrit aucune machine virtuelle.";
                return plan;
            }
            if (systems.Count > 1)
                plan.Notes.Add($"Le paquet contient {systems.Count} machines : seule la première, « {NameOf(system)} », est importée.");

            plan.Name = NameOf(system);
            ReadGuestOs(plan, Descendants(system, "OperatingSystemSection").FirstOrDefault());

            var profile = plan.Profile;
            var hardware = Descendants(system, "VirtualHardwareSection").FirstOrDefault();
            var items = hardware is null
                ? new List<XElement>()
                : hardware.Elements().Where(e => e.Name.LocalName is "Item" or "StorageItem" or "EthernetPortItem").ToList();

            plan.MemoryMb = profile.MemoryMb;
            plan.CpuCount = profile.CpuCount;
            foreach (var item in items)
            {
                switch (ResourceType(item))
                {
                    case 3:
                        plan.CpuCount = (int)Math.Max(1, Number(item, "VirtualQuantity") ?? plan.CpuCount);
                        var cores = (int)(Number(item, "CoresPerSocket") ?? 1);
                        plan.CoresPerSocket = cores > 1 && plan.CpuCount % cores == 0 ? cores : 1;
                        break;
                    case 4:
                        if (Number(item, "VirtualQuantity") is { } quantity)
                            plan.MemoryMb = (int)Math.Max(16, quantity * MegabytesPer(Child(item, "AllocationUnits") ?? "byte * 2^20"));
                        break;
                }
            }

            plan.Firmware = IsEfi(system) ? FirmwareType.Uefi : FirmwareType.Bios;

            ReadDisks(plan, root, items, locate);
            ReadAdapters(plan, items);
            return plan;
        }

        private static string NameOf(XElement system) =>
            Child(system, "Name") is { Length: > 0 } name
                ? name
                : Attribute(system, "id") ?? "Machine importée";

        private static void ReadGuestOs(ImportPlan plan, XElement? section)
        {
            if (section is null)
                return;

            var vmware = Attribute(section, "osType");
            var virtualBox = Child(section, "OSType");
            var description = Child(section, "Description") ?? string.Empty;
            plan.SourceGuestOs = vmware ?? virtualBox ?? description;
            plan.GuestOs = GuestOsMapping.FromOvf(vmware, virtualBox, ParseLong(Attribute(section, "id")), description);
        }

        /// <summary>
        /// UEFI as VMware writes it (a vmw:Config item, key "firmware") or as VirtualBox does (its own machine
        /// XML, embedded under vbox:Machine, with a Firmware element). Plain OVF has no word for it.
        /// </summary>
        private static bool IsEfi(XElement system) =>
            Descendants(system, "Config").Any(c =>
                string.Equals(Attribute(c, "key"), "firmware", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Attribute(c, "value"), "efi", StringComparison.OrdinalIgnoreCase))
            || Descendants(system, "Firmware").Any(f =>
                (Attribute(f, "type") ?? string.Empty).StartsWith("EFI", StringComparison.OrdinalIgnoreCase));

        private sealed record Controller(string InstanceId, string Kind, string SubType, int Order);

        private static void ReadDisks(ImportPlan plan, XElement root, List<XElement> items, Func<string, DiskFile?> locate)
        {
            var files = Descendants(root, "References").Elements().Where(e => e.Name.LocalName == "File")
                .Where(f => Attribute(f, "id") is not null)
                .GroupBy(f => Attribute(f, "id")!)
                .ToDictionary(g => g.Key, g => g.First());
            var disks = Descendants(root, "DiskSection").Elements().Where(e => e.Name.LocalName == "Disk")
                .Where(d => Attribute(d, "diskId") is not null)
                .GroupBy(d => Attribute(d, "diskId")!)
                .ToDictionary(g => g.Key, g => g.First());

            var controllers = new Dictionary<string, Controller>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var kind = ResourceType(item) switch
                {
                    5 => "ide",
                    6 => "scsi",
                    20 => "sata",
                    _ => null
                };
                if (kind is not null && Child(item, "InstanceID") is { } id)
                    controllers[id] = new Controller(id, kind, Child(item, "ResourceSubType") ?? string.Empty, controllers.Count);
            }

            var found = new List<(int Rank, int Controller, long Unit, ImportedDisk Disk)>();
            foreach (var item in items.Where(i => ResourceType(i) == 17))
            {
                // "ovf:/disk/vmdisk1" names a disk of the DiskSection; "ovf:/file/file1" a file directly.
                var resource = Child(item, "HostResource") ?? string.Empty;
                XElement? file = null;
                var format = string.Empty;
                if (resource.Contains("/disk/", StringComparison.OrdinalIgnoreCase)
                    && disks.TryGetValue(resource[(resource.LastIndexOf('/') + 1)..], out var disk))
                {
                    format = Attribute(disk, "format") ?? string.Empty;
                    if (Attribute(disk, "fileRef") is { } fileRef)
                        files.TryGetValue(fileRef, out file);
                    if (file is null)
                    {
                        // A disk with no file is an empty one the importer is meant to create.
                        VmxReader.AddOnce(plan.Notes, "Un disque vide du paquet (sans fichier) n'est pas créé : ajoutez-le dans Matériel si besoin.");
                        continue;
                    }
                }
                else if (resource.Contains("/file/", StringComparison.OrdinalIgnoreCase))
                {
                    files.TryGetValue(resource[(resource.LastIndexOf('/') + 1)..], out file);
                }
                if (file is null)
                    continue;

                var href = Attribute(file, "href") ?? string.Empty;
                if (Attribute(file, "compression") is { Length: > 0 } compression)
                {
                    plan.Blocker ??= $"Le disque {href} est compressé dans le paquet ({compression}) : QEMU ne sait pas le lire ainsi. " +
                                     "Réexportez la machine sans compression des fichiers.";
                    continue;
                }
                if (Attribute(file, "chunkSize") is { Length: > 0 })
                {
                    plan.Blocker ??= $"Le disque {href} est découpé en morceaux dans le paquet : réexportez la machine sans découpage.";
                    continue;
                }

                var source = locate(href);
                if (source is null)
                {
                    plan.Blocker ??= $"Disque introuvable dans le paquet : {href}";
                    continue;
                }

                var driver = DriverFor(format, href);
                if (driver is null)
                {
                    plan.Blocker ??= $"Le disque {href} est dans un format que WinQEMU ne connaît pas ({format}).";
                    continue;
                }

                controllers.TryGetValue(Child(item, "Parent") ?? string.Empty, out var controller);
                var bus = BusFor(controller, plan);
                found.Add((Rank(controller?.Kind, controller?.SubType), controller?.Order ?? int.MaxValue,
                    ParseLong(Child(item, "AddressOnParent")) ?? 0, new ImportedDisk
                    {
                        SourcePath = source.Path,
                        QemuSource = source.Entry is null ? null : JsonSource(driver, source.Path, source.Entry),
                        PackageEntry = source.Entry?.Name,
                        Bus = bus,
                        SourceSlot = controller is null
                            ? href
                            : $"{href} ({controller.Kind}{(controller.SubType.Length > 0 ? " " + controller.SubType : string.Empty)})"
                    }));
            }

            foreach (var entry in found.OrderBy(f => f.Rank).ThenBy(f => f.Controller).ThenBy(f => f.Unit))
                plan.Disks.Add(entry.Disk);
        }

        /// <summary>
        /// A disk inside the package as qemu-img opens it in place: the format driver over raw's offset and
        /// size window on the .ova (verified: a streamOptimized vmdk read this way is identical byte for byte).
        /// </summary>
        internal static string JsonSource(string driver, string packagePath, TarEntry entry) =>
            "json:" + JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["driver"] = driver,
                ["file"] = new Dictionary<string, object>
                {
                    ["driver"] = "raw",
                    ["offset"] = entry.DataOffset,
                    ["size"] = entry.Size,
                    ["file"] = new Dictionary<string, object> { ["driver"] = "file", ["filename"] = packagePath }
                }
            });

        /// <summary>QEMU's driver for an OVF disk format URI, or the file's extension; null when unknown.</summary>
        private static string? DriverFor(string format, string href)
        {
            var hint = (format + " " + Path.GetExtension(href)).ToLowerInvariant();
            if (hint.Contains("vmdk", StringComparison.Ordinal))
                return "vmdk";
            if (hint.Contains("vhdx", StringComparison.Ordinal))
                return "vhdx";
            if (hint.Contains("vhd", StringComparison.Ordinal))
                return "vpc";
            if (hint.Contains("qcow", StringComparison.Ordinal))
                return "qcow2";
            if (hint.Contains(".img", StringComparison.Ordinal) || hint.Contains("raw", StringComparison.Ordinal))
                return "raw";
            return null;
        }

        private static int Rank(string? kind, string? subType) => kind switch
        {
            "sata" when (subType ?? string.Empty).Contains("nvme", StringComparison.OrdinalIgnoreCase) => 0,
            "scsi" => 1,
            "sata" => 2,
            _ => 3
        };

        /// <summary>The source's controller where QEMU has it, as for a .vmx: Windows boots only from its own.</summary>
        private static DiskBus BusFor(Controller? controller, ImportPlan plan)
        {
            if (controller is null)
                return plan.Profile.DiskBus;

            var subType = controller.SubType.ToLowerInvariant();
            switch (controller.Kind)
            {
                case "ide":
                    return DiskBus.Ide;
                case "sata":
                    return subType.Contains("nvme", StringComparison.Ordinal) ? DiskBus.Nvme : DiskBus.Sata;
            }

            if (subType == "lsilogicsas")
                return DiskBus.LsiSas;
            if (subType is "virtualscsi" or "pvscsi")
                return DiskBus.Pvscsi;

            VmxReader.AddOnce(plan.Notes,
                $"Le contrôleur SCSI « {(subType.Length == 0 ? "lsilogic" : subType)} » n'a pas d'équivalent ici : les disques passent en SATA." +
                (plan.IsWindows
                    ? " Windows peut alors refuser de démarrer (INACCESSIBLE_BOOT_DEVICE) : activez au préalable son pilote AHCI " +
                      "dans la machine source (sc config storahci start= boot)."
                    : string.Empty));
            return DiskBus.Sata;
        }

        private static void ReadAdapters(ImportPlan plan, List<XElement> items)
        {
            foreach (var item in items.Where(i => ResourceType(i) == 10))
            {
                var subType = (Child(item, "ResourceSubType") ?? string.Empty).ToLowerInvariant();
                var model = subType switch
                {
                    "vmxnet3" => NicModel.Vmxnet3,
                    "e1000e" => NicModel.E1000e,
                    "e1000" => NicModel.E1000,
                    _ when subType.Contains("virtio", StringComparison.Ordinal) => NicModel.VirtioNet,
                    _ => plan.Profile.NicModel
                };
                plan.Adapters.Add(new ImportedAdapter
                {
                    Model = model,
                    MacAddress = VmxReader.NormalizeMac(Child(item, "Address")),
                    Mode = NetworkMode.Nat
                });
            }

            if (plan.Adapters.Count > 0)
                plan.Notes.Add("Les cartes réseau passent en NAT : changez leur connexion dans Matériel si besoin.");
        }

        /// <summary>Megabytes in one unit of a CIM programmatic unit ("byte * 2^20", "MegaBytes", "GB"…).</summary>
        internal static double MegabytesPer(string units)
        {
            var text = units.Trim().ToLowerInvariant();
            var power = Regex.Match(text, @"^byte\s*\*\s*2\s*\^\s*(\d+)$");
            if (power.Success)
                return Math.Pow(2, int.Parse(power.Groups[1].Value, CultureInfo.InvariantCulture) - 20);
            return text switch
            {
                "byte" or "bytes" => 1d / (1024 * 1024),
                "kilobytes" or "kb" or "kib" => 1d / 1024,
                "gigabytes" or "gb" or "gib" => 1024,
                _ => 1
            };
        }

        private static int ResourceType(XElement item) => (int)(ParseLong(Child(item, "ResourceType")) ?? 0);

        private static double? Number(XElement item, string name) =>
            double.TryParse(Child(item, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

        private static long? ParseLong(string? text) =>
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        private static string? Child(XElement element, string localName) =>
            element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

        private static string? Attribute(XElement element, string localName) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

        private static IEnumerable<XElement> Descendants(XElement element, string localName) =>
            element.Descendants().Where(e => e.Name.LocalName == localName);
    }
}
