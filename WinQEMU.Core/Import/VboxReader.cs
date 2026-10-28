using System.Globalization;
using System.Xml.Linq;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Import
{
    /// <summary>
    /// Reads a VirtualBox machine from its .vbox. Elements are found by local name anywhere under the
    /// machine rather than by full path: VirtualBox 7 moved several of them (the CPU under a platform
    /// element, for one), and the names did not change.
    /// </summary>
    public static class VboxReader
    {
        public static ImportPlan Read(string vboxPath)
        {
            var full = Path.GetFullPath(vboxPath);
            var plan = Parse(File.ReadAllText(full), full);

            foreach (var disk in plan.Disks.Where(d => !File.Exists(d.SourcePath)))
                plan.Blocker ??= string.Format(CultureInfo.CurrentCulture, PlatformText.ImportDiskNotFound, disk.SourcePath);

            return plan;
        }

        /// <param name="vboxPath">Absolute path of the .vbox, against which relative locations resolve.</param>
        public static ImportPlan Parse(string xml, string vboxPath)
        {
            var document = XDocument.Parse(xml);
            var machine = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Machine")
                          ?? throw new FormatException(PlatformText.ImportVboxNoMachine);
            var folder = Path.GetDirectoryName(vboxPath)!;
            string Resolve(string location) => Path.GetFullPath(Path.Combine(folder, location));

            var plan = new ImportPlan
            {
                Source = ImportSource.VirtualBox,
                SourcePath = vboxPath,
                Name = (string?)machine.Attribute("name") ?? Path.GetFileNameWithoutExtension(vboxPath),
                SourceGuestOs = (string?)machine.Attribute("OSType") ?? string.Empty
            };
            plan.GuestOs = GuestOsMapping.FromVirtualBox(plan.SourceGuestOs);

            var profile = plan.Profile;
            plan.MemoryMb = IntAttribute(Find(machine, "Memory"), "RAMSize") ?? profile.MemoryMb;
            // VirtualBox leaves count out at its default of one.
            plan.CpuCount = IntAttribute(Find(machine, "CPU"), "count") ?? 1;
            plan.Firmware = ((string?)Find(machine, "Firmware")?.Attribute("type") ?? "BIOS")
                .StartsWith("EFI", StringComparison.OrdinalIgnoreCase)
                    ? FirmwareType.Uefi
                    : FirmwareType.Bios;

            if (machine.Attribute("stateFile") is not null)
            {
                plan.Notes.Add(PlatformText.ImportVboxSavedState);
            }

            ReadStorage(plan, machine, Resolve);
            ReadAdapters(plan, machine);
            return plan;
        }

        private static void ReadStorage(ImportPlan plan, XElement machine, Func<string, string> resolve)
        {
            // Every medium is registered once, by uuid; differencing images are nested in their parent.
            var hardDisks = machine.Descendants().Where(e => e.Name.LocalName == "HardDisk").ToList();
            var images = machine.Descendants().Where(e => e.Name.LocalName == "Image" && e.Parent?.Name.LocalName == "DVDImages").ToList();

            foreach (var controller in machine.Descendants().Where(e => e.Name.LocalName == "StorageController"))
            {
                var type = (string?)controller.Attribute("type") ?? string.Empty;
                var bus = BusFor(type);

                foreach (var attached in controller.Elements().Where(e => e.Name.LocalName == "AttachedDevice")
                             .OrderBy(e => IntAttribute(e, "port") ?? 0).ThenBy(e => IntAttribute(e, "device") ?? 0))
                {
                    var uuid = (string?)attached.Elements().FirstOrDefault(e => e.Name.LocalName == "Image")?.Attribute("uuid");
                    if (uuid is null)
                        continue;

                    var deviceType = (string?)attached.Attribute("type");
                    if (string.Equals(deviceType, "DVD", StringComparison.OrdinalIgnoreCase))
                    {
                        var location = (string?)images.FirstOrDefault(i => SameUuid(i, uuid))?.Attribute("location");
                        if (string.IsNullOrEmpty(plan.IsoPath) && location is not null && File.Exists(resolve(location)))
                            plan.IsoPath = resolve(location);
                        continue;
                    }

                    if (!string.Equals(deviceType, "HardDisk", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var medium = hardDisks.FirstOrDefault(h => SameUuid(h, uuid));
                    if (medium is null)
                    {
                        plan.Blocker ??= string.Format(CultureInfo.CurrentCulture, PlatformText.ImportVboxDiskNotDeclared, uuid);
                        continue;
                    }

                    if (medium.Parent?.Name.LocalName == "HardDisk")
                    {
                        // Its current state is a differencing image on top of the snapshots, and QEMU's VDI
                        // driver reads base images only.
                        plan.Blocker ??= PlatformText.ImportVboxDifferencingDisk;
                        continue;
                    }

                    if (bus == DiskBus.Sata && !type.Equals("AHCI", StringComparison.OrdinalIgnoreCase))
                    {
                        VmxReader.AddOnce(plan.Notes,
                            string.Format(CultureInfo.CurrentCulture, PlatformText.ImportVboxControllerToSata, type) +
                            (plan.IsWindows
                                ? " " + PlatformText.ImportAhciHintVirtualBox
                                : string.Empty));
                    }

                    plan.Disks.Add(new ImportedDisk
                    {
                        SourcePath = resolve((string?)medium.Attribute("location") ?? string.Empty),
                        Bus = bus,
                        SourceSlot = $"{(string?)controller.Attribute("name") ?? type}, port {IntAttribute(attached, "port") ?? 0}"
                    });
                }
            }
        }

        /// <summary>The same controller where QEMU has it, for the reason given on <see cref="DiskBus.LsiSas"/>.</summary>
        private static DiskBus BusFor(string controllerType) => controllerType.ToUpperInvariant() switch
        {
            "AHCI" => DiskBus.Sata,
            "PIIX3" or "PIIX4" or "ICH6" => DiskBus.Ide,
            "NVME" => DiskBus.Nvme,
            "LSILOGICSAS" => DiskBus.LsiSas,
            "VIRTIOSCSI" => DiskBus.VirtioBlk,
            // LsiLogic (parallel 53c1030) and BusLogic: QEMU emulates neither.
            _ => DiskBus.Sata
        };

        private static void ReadAdapters(ImportPlan plan, XElement machine)
        {
            var adapters = machine.Descendants()
                .Where(e => e.Name.LocalName == "Adapter" && e.Parent?.Name.LocalName == "Network")
                .Where(e => string.Equals((string?)e.Attribute("enabled"), "true", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => IntAttribute(e, "slot") ?? 0);

            foreach (var adapter in adapters)
            {
                var type = (string?)adapter.Attribute("type") ?? "82540EM";
                var model = type.ToLowerInvariant() switch
                {
                    "virtio" => NicModel.VirtioNet,
                    _ => NicModel.E1000
                };
                if (type.StartsWith("Am79", StringComparison.OrdinalIgnoreCase))
                {
                    VmxReader.AddOnce(plan.Notes, PlatformText.ImportVboxPcnet);
                }

                var attachment = adapter.Elements().Select(e => e.Name.LocalName)
                    .FirstOrDefault(n => n is "NAT" or "BridgedInterface" or "InternalNetwork" or "HostOnlyInterface" or "NATNetwork" or "GenericInterface");
                if (attachment is not null and not "NAT")
                {
                    VmxReader.AddOnce(plan.Notes, PlatformText.ImportVboxAdapterToNat);
                }

                plan.Adapters.Add(new ImportedAdapter
                {
                    Model = model,
                    MacAddress = VmxReader.NormalizeMac((string?)adapter.Attribute("MACAddress")),
                    Mode = NetworkMode.Nat
                });
            }
        }

        private static XElement? Find(XElement root, string localName) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

        private static bool SameUuid(XElement element, string uuid) =>
            string.Equals(((string?)element.Attribute("uuid"))?.Trim('{', '}'), uuid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);

        private static int? IntAttribute(XElement? element, string name) =>
            int.TryParse((string?)element?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : null;
    }
}
