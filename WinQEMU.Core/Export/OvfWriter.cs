using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Export
{
    /// <summary>A disk as it goes into the package: its file there, its size, and where it hangs.</summary>
    public sealed record ExportedDisk(string FileName, long FileSize, long CapacityBytes, ExportBus Bus);

    /// <summary>The controllers every hypervisor that reads OVF knows; QEMU's VirtIO is not among them.</summary>
    public enum ExportBus { Sata, Scsi, ScsiParavirtual, Nvme, Ide }

    /// <summary>
    /// Writes the OVF 1.0 descriptor of an exported machine the way VMware's own exports look, since its
    /// importer is the strictest: hardware family "vmx-14", SATA as "vmware.sata.ahci" (VMware refuses
    /// VirtualBox's "AHCI"), the guest named by vmw:osType, and UEFI by a vmw:Config item. VirtualBox also
    /// reads its own vbox:OSType, written next to it. rasd elements are in their schema's alphabetical order.
    /// </summary>
    public static class OvfWriter
    {
        private static readonly XNamespace Ovf = "http://schemas.dmtf.org/ovf/envelope/1";
        private static readonly XNamespace Rasd = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData";
        private static readonly XNamespace Vssd = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_VirtualSystemSettingData";
        private static readonly XNamespace Vmw = "http://www.vmware.com/schema/ovf";
        private static readonly XNamespace Vbox = "http://www.virtualbox.org/ovf/machine";
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace Cim = "http://schemas.dmtf.org/wbem/wscim/1/common";

        public const string NetworkName = "nat";

        public static XDocument Build(VmConfig config, IReadOnlyList<ExportedDisk> disks)
        {
            var name = config.Name.Trim();
            var family = GuestOsCatalog.Find(config.GuestOs)?.Family;
            var (vmwareOs, virtualBoxOs, cimOs) = GuestIds(config.GuestOs);

            // VirtualBox ties its own machine configuration's disks to the DiskSection by these.
            var diskUuids = disks.Select(_ => Guid.NewGuid()).ToList();
            var references = new XElement(Ovf + "References");
            var diskSection = new XElement(Ovf + "DiskSection", new XElement(Ovf + "Info", "Virtual disk information"));
            for (var i = 0; i < disks.Count; i++)
            {
                var n = (i + 1).ToString(CultureInfo.InvariantCulture);
                references.Add(new XElement(Ovf + "File",
                    new XAttribute(Ovf + "href", disks[i].FileName),
                    new XAttribute(Ovf + "id", "file" + n),
                    new XAttribute(Ovf + "size", disks[i].FileSize)));
                diskSection.Add(new XElement(Ovf + "Disk",
                    new XAttribute(Ovf + "capacity", disks[i].CapacityBytes),
                    new XAttribute(Ovf + "capacityAllocationUnits", "byte"),
                    new XAttribute(Ovf + "diskId", "vmdisk" + n),
                    new XAttribute(Ovf + "fileRef", "file" + n),
                    new XAttribute(Ovf + "format", "http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized"),
                    new XAttribute(Vbox + "uuid", diskUuids[i].ToString())));
            }

            var hardware = new XElement(Ovf + "VirtualHardwareSection",
                new XElement(Ovf + "Info", "Virtual hardware requirements"),
                new XElement(Ovf + "System",
                    new XElement(Vssd + "ElementName", "Virtual Hardware Family"),
                    new XElement(Vssd + "InstanceID", 0),
                    new XElement(Vssd + "VirtualSystemIdentifier", name),
                    new XElement(Vssd + "VirtualSystemType", "vmx-14")));

            var instance = 0;
            var cpu = Item(++instance, 3, $"{config.CpuCount} virtual CPU(s)", "Number of Virtual CPUs",
                allocationUnits: "hertz * 10^6", quantity: config.CpuCount);
            if (config.CoresPerSocket > 1 && config.CpuCount % config.CoresPerSocket == 0)
                cpu.Add(new XElement(Vmw + "CoresPerSocket", new XAttribute(Ovf + "required", "false"), config.CoresPerSocket));
            hardware.Add(cpu);
            hardware.Add(Item(++instance, 4, $"{config.MemoryMb}MB of memory", "Memory Size",
                allocationUnits: "byte * 2^20", quantity: config.MemoryMb));

            // One controller per kind, in the order the disks need them; IDE holds two disks per controller.
            var controllers = new Dictionary<(ExportBus, int), int>();
            var unitsUsed = new Dictionary<(ExportBus, int), int>();
            var diskItems = new List<XElement>();
            for (var i = 0; i < disks.Count; i++)
            {
                var bus = disks[i].Bus;
                var group = bus == ExportBus.Ide ? disks.Take(i).Count(d => d.Bus == ExportBus.Ide) / 2 : 0;
                var key = (bus, group);
                if (!controllers.TryGetValue(key, out var controllerId))
                {
                    controllerId = ++instance;
                    controllers[key] = controllerId;
                    hardware.Add(ControllerItem(controllerId, bus, group));
                }

                var unit = unitsUsed.GetValueOrDefault(key);
                // SCSI unit 7 is the controller's own.
                if (bus is ExportBus.Scsi or ExportBus.ScsiParavirtual && unit == 7)
                    unit++;
                unitsUsed[key] = unit + 1;

                diskItems.Add(new XElement(Ovf + "Item",
                    new XElement(Rasd + "AddressOnParent", unit),
                    new XElement(Rasd + "ElementName", $"Hard Disk {i + 1}"),
                    new XElement(Rasd + "HostResource", $"ovf:/disk/vmdisk{i + 1}"),
                    new XElement(Rasd + "InstanceID", 0),
                    new XElement(Rasd + "Parent", controllerId),
                    new XElement(Rasd + "ResourceType", 17)));
            }
            foreach (var item in diskItems)
            {
                item.Element(Rasd + "InstanceID")!.Value = (++instance).ToString(CultureInfo.InvariantCulture);
                hardware.Add(item);
            }

            var adapters = config.NetworkAdapters.Where(a => a.Mode != NetworkMode.None).ToList();
            for (var i = 0; i < adapters.Count; i++)
            {
                var adapter = adapters[i];
                var item = new XElement(Ovf + "Item");
                if (adapter.MacAddress.Length > 0)
                    item.Add(new XElement(Rasd + "Address", adapter.MacAddress));
                item.Add(
                    new XElement(Rasd + "AddressOnParent", i + 1),
                    new XElement(Rasd + "AutomaticAllocation", adapter.Connected ? "true" : "false"),
                    new XElement(Rasd + "Connection", NetworkName),
                    new XElement(Rasd + "Description", $"{NicSubType(adapter.Model, family)} ethernet adapter on \"{NetworkName}\""),
                    new XElement(Rasd + "ElementName", $"Network adapter {i + 1}"),
                    new XElement(Rasd + "InstanceID", ++instance),
                    new XElement(Rasd + "ResourceSubType", NicSubType(adapter.Model, family)),
                    new XElement(Rasd + "ResourceType", 10));
                hardware.Add(item);
            }

            if (config.Firmware == FirmwareType.Uefi)
            {
                hardware.Add(new XElement(Vmw + "Config",
                    new XAttribute(Ovf + "required", "false"),
                    new XAttribute(Vmw + "key", "firmware"),
                    new XAttribute(Vmw + "value", "efi")));
            }

            var system = new XElement(Ovf + "VirtualSystem",
                new XAttribute(Ovf + "id", name),
                new XElement(Ovf + "Info", "A virtual machine"),
                new XElement(Ovf + "Name", name));
            if (!string.IsNullOrWhiteSpace(config.Description))
            {
                system.Add(new XElement(Ovf + "AnnotationSection",
                    new XElement(Ovf + "Info", "A human-readable annotation"),
                    new XElement(Ovf + "Annotation", config.Description.Trim())));
            }
            system.Add(
                new XElement(Ovf + "OperatingSystemSection",
                    new XAttribute(Ovf + "id", cimOs),
                    new XAttribute(Vmw + "osType", vmwareOs),
                    new XElement(Ovf + "Info", "The kind of installed guest operating system"),
                    new XElement(Vbox + "OSType", new XAttribute(Ovf + "required", "false"), virtualBoxOs)),
                hardware,
                VirtualBoxMachine(config, name, virtualBoxOs, family, disks, diskUuids));

            // OVF as the default namespace, as VMware and VirtualBox write it: VirtualBox's machine
            // configuration below is unprefixed in its own exports.
            var envelope = new XElement(Ovf + "Envelope",
                new XAttribute("xmlns", Ovf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "ovf", Ovf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "rasd", Rasd.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "vssd", Vssd.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "vmw", Vmw.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "vbox", Vbox.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "cim", Cim.NamespaceName),
                new XAttribute(Ovf + "version", "1.0"),
                references,
                diskSection,
                new XElement(Ovf + "NetworkSection",
                    new XElement(Ovf + "Info", "The list of logical networks"),
                    new XElement(Ovf + "Network", new XAttribute(Ovf + "name", NetworkName),
                        new XElement(Ovf + "Description", $"The {NetworkName} network"))),
                system);

            return new XDocument(new XDeclaration("1.0", "UTF-8", null), envelope);
        }

        /// <summary>The descriptor's bytes: UTF-8 without a byte order mark, which some importers choke on.</summary>
        public static byte[] Serialize(XDocument document)
        {
            using var memory = new MemoryStream();
            using (var writer = XmlWriter.Create(memory, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                document.Save(writer);
            return memory.ToArray();
        }

        /// <summary>
        /// VirtualBox's own configuration of the machine, which its importer uses instead of the CIM items
        /// when present. It is the only place VirtualBox reads the firmware from: without it a UEFI Debian
        /// exported from here was imported with the BIOS of the Debian type and stopped on "No bootable
        /// medium found!". Settings format 1.16 (VirtualBox 6.0), which later versions convert on reading.
        /// </summary>
        private static XElement VirtualBoxMachine(
            VmConfig config, string name, string osType, string? family, IReadOnlyList<ExportedDisk> disks, IReadOnlyList<Guid> diskUuids)
        {
            XElement E(string local, params object[] content) => new(Ovf + local, content);

            var controllers = new List<XElement>();
            var byBus = new Dictionary<ExportBus, XElement>();
            for (var i = 0; i < disks.Count; i++)
            {
                // VirtualBox has no paravirtual SCSI: SATA, as for VirtIO.
                var bus = disks[i].Bus == ExportBus.ScsiParavirtual ? ExportBus.Sata : disks[i].Bus;
                if (!byBus.TryGetValue(bus, out var controller))
                {
                    var (controllerName, type) = bus switch
                    {
                        ExportBus.Ide => ("IDE", "PIIX4"),
                        ExportBus.Scsi => ("SAS", "LsiLogicSas"),
                        ExportBus.Nvme => ("NVMe", "NVMe"),
                        _ => ("SATA", "AHCI")
                    };
                    controller = E("StorageController",
                        new XAttribute("name", controllerName),
                        new XAttribute("type", type),
                        new XAttribute("PortCount", bus == ExportBus.Ide ? 2 : 1),
                        new XAttribute("useHostIOCache", bus == ExportBus.Ide ? "true" : "false"),
                        new XAttribute("Bootable", "true"));
                    byBus[bus] = controller;
                    controllers.Add(controller);
                }

                var index = controller.Elements().Count();
                controller.Add(E("AttachedDevice",
                    new XAttribute("type", "HardDisk"),
                    new XAttribute("hotpluggable", "false"),
                    new XAttribute("port", bus == ExportBus.Ide ? index / 2 : index),
                    new XAttribute("device", bus == ExportBus.Ide ? index % 2 : 0),
                    E("Image", new XAttribute("uuid", "{" + diskUuids[i] + "}"))));
            }
            foreach (var controller in controllers.Where(c => (string?)c.Attribute("type") != "PIIX4"))
                controller.SetAttributeValue("PortCount", Math.Max(1, controller.Elements().Count()));

            var network = E("Network");
            var adapters = config.NetworkAdapters.Where(a => a.Mode != NetworkMode.None).ToList();
            for (var i = 0; i < adapters.Count; i++)
            {
                var adapter = E("Adapter",
                    new XAttribute("slot", i),
                    new XAttribute("enabled", "true"),
                    new XAttribute("cable", adapters[i].Connected ? "true" : "false"),
                    new XAttribute("type", VirtualBoxNic(adapters[i].Model, family)),
                    E("NAT"));
                if (adapters[i].MacAddress.Length > 0)
                    adapter.Add(new XAttribute("MACAddress", adapters[i].MacAddress.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant()));
                network.Add(adapter);
            }

            var hardware = E("Hardware",
                E("CPU", new XAttribute("count", config.CpuCount),
                    E("PAE", new XAttribute("enabled", "true")),
                    E("LongMode", new XAttribute("enabled", "true")),
                    E("X2APIC", new XAttribute("enabled", "true"))),
                E("Memory", new XAttribute("RAMSize", config.MemoryMb)),
                E("Firmware", new XAttribute("type", config.Firmware == FirmwareType.Uefi ? "EFI" : "BIOS")),
                E("Boot",
                    E("Order", new XAttribute("position", 1), new XAttribute("device", "HardDisk")),
                    E("Order", new XAttribute("position", 2), new XAttribute("device", "DVD"))),
                E("Display",
                    new XAttribute("controller", family == "Windows" ? "VBoxSVGA" : "VMSVGA"),
                    new XAttribute("VRAMSize", 128)),
                E("RTC", new XAttribute("localOrUTC", config.RtcLocalTime ? "local" : "UTC")),
                network,
                E("StorageControllers", controllers));

            return new XElement(Vbox + "Machine",
                new XAttribute(Ovf + "required", "false"),
                new XAttribute("version", "1.16-windows"),
                new XAttribute("uuid", "{" + Guid.NewGuid() + "}"),
                new XAttribute("name", name),
                new XAttribute("OSType", osType),
                new XAttribute("snapshotFolder", "Snapshots"),
                E("Info", "Complete VirtualBox machine configuration in VirtualBox format"),
                hardware);
        }

        /// <summary>
        /// VirtualBox's card: VirtIO itself for Linux, which has its driver; the Intel PRO/1000 MT Desktop for
        /// Windows, VirtualBox's own default there; the PRO/1000 MT Server (its "E1000") otherwise.
        /// </summary>
        private static string VirtualBoxNic(NicModel model, string? family) => model switch
        {
            NicModel.VirtioNet when family == "Linux" => "virtio",
            _ when family == "Windows" => "82540EM",
            _ => "82545EM"
        };

        private static XElement Item(int instance, int resourceType, string elementName, string description,
            string allocationUnits, int quantity) =>
            new(Ovf + "Item",
                new XElement(Rasd + "AllocationUnits", allocationUnits),
                new XElement(Rasd + "Description", description),
                new XElement(Rasd + "ElementName", elementName),
                new XElement(Rasd + "InstanceID", instance),
                new XElement(Rasd + "ResourceType", resourceType),
                new XElement(Rasd + "VirtualQuantity", quantity));

        private static XElement ControllerItem(int instance, ExportBus bus, int index)
        {
            var (type, subType, label) = bus switch
            {
                ExportBus.Ide => (5, "PIIX4", "IDE Controller"),
                ExportBus.Scsi => (6, "lsilogicsas", "SCSI Controller"),
                ExportBus.ScsiParavirtual => (6, "VirtualSCSI", "SCSI Controller"),
                ExportBus.Nvme => (20, "vmware.nvme.controller", "NVMe Controller"),
                _ => (20, "vmware.sata.ahci", "SATA Controller")
            };
            return new XElement(Ovf + "Item",
                new XElement(Rasd + "Address", index),
                new XElement(Rasd + "Description", label),
                new XElement(Rasd + "ElementName", $"{label} {index}"),
                new XElement(Rasd + "InstanceID", instance),
                new XElement(Rasd + "ResourceSubType", subType),
                new XElement(Rasd + "ResourceType", type));
        }

        /// <summary>
        /// The controller a disk is exported on. VirtIO has no equivalent in VMware or VirtualBox: SATA, whose
        /// driver every Windows and Linux carries.
        /// </summary>
        public static ExportBus BusFor(DiskBus bus) => bus switch
        {
            DiskBus.Ide => ExportBus.Ide,
            DiskBus.Nvme => ExportBus.Nvme,
            DiskBus.LsiSas => ExportBus.Scsi,
            DiskBus.Pvscsi => ExportBus.ScsiParavirtual,
            _ => ExportBus.Sata
        };

        /// <summary>
        /// The card model in OVF terms. VirtIO and the RTL8139 exist in neither VMware nor (for the first)
        /// every VirtualBox: an Intel card instead, the e1000e for Windows, which ships no driver for the
        /// e1000 since Windows 8.
        /// </summary>
        public static string NicSubType(NicModel model, string? family) => model switch
        {
            NicModel.Vmxnet3 => "VmxNet3",
            NicModel.E1000 => "E1000",
            NicModel.E1000e => "E1000e",
            _ => family == "Windows" ? "E1000e" : "E1000"
        };

        /// <summary>VMware's vSphere guest id, VirtualBox's OSType and the CIM operating system number.</summary>
        private static (string VMware, string VirtualBox, int Cim) GuestIds(string guestOs) => guestOs switch
        {
            "windows-11" => ("windows11_64Guest", "Windows11_64", 1),
            "windows-10" => ("windows9_64Guest", "Windows10_64", 1),
            "windows-server" => ("windows2019srv_64Guest", "Windows2019_64", 1),
            "windows-7" => ("windows7_64Guest", "Windows7_64", 105),
            "ubuntu" => ("ubuntu64Guest", "Ubuntu_64", 94),
            "debian" => ("debian12_64Guest", "Debian_64", 96),
            "fedora" => ("rhel9_64Guest", "RedHat_64", 80),
            "linux" or "linux-arm64" => ("otherLinux64Guest", "Linux_64", 101),
            _ => ("otherGuest64", "Other_64", 102)
        };
    }
}
