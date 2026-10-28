using System.Security.Cryptography;
using System.Text;
using WinQEMU.Core.Export;
using WinQEMU.Core.Import;
using WinQEMU.Core.Models;
using System.Xml.Linq;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>OVF packages: the tar they are, descriptors as VMware and VirtualBox write them, and a round trip.</summary>
public class OvaTests : IAsyncLifetime
{
    private string _folder = string.Empty;
    private QemuImgService? _images;

    public async Task InitializeAsync()
    {
        var detection = await QemuLocator.DetectAsync();
        if (detection.Installation is not null)
            _images = new QemuImgService(detection.Installation);
        _folder = Path.Combine(Path.GetTempPath(), "winqemu-ova-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    // ---- The tar ----

    [Fact]
    public void Entries_are_found_where_their_bytes_lie()
    {
        using var tar = new MemoryStream();
        foreach (var (name, content) in new[] { ("a.ovf", "<Envelope/>"), ("a-disk1.vmdk", new string('x', 1000)) })
        {
            var bytes = Encoding.ASCII.GetBytes(content);
            TarArchive.WriteHeader(tar, name, bytes.Length, DateTimeOffset.UnixEpoch);
            tar.Write(bytes);
            TarArchive.WritePadding(tar, bytes.Length);
        }
        TarArchive.WriteEnd(tar);

        tar.Position = 0;
        var entries = TarArchive.ReadEntries(tar);

        Assert.Equal(new[] { "a.ovf", "a-disk1.vmdk" }, entries.Select(e => e.Name));
        Assert.Equal(512, entries[0].DataOffset);
        Assert.Equal(1536, entries[1].DataOffset);
        Assert.Equal(1000, entries[1].Size);
        Assert.Equal((byte)'x', tar.ToArray()[entries[1].DataOffset]);
        Assert.Equal(0, tar.Length % 512);
    }

    [Fact]
    public void A_disk_over_8_GiB_keeps_its_size()
    {
        // Eleven octal digits stop at 8 GiB; beyond, the size is written in GNU's base-256 form.
        const long size = 20L * 1024 * 1024 * 1024 + 123;
        using var tar = new MemoryStream();
        TarArchive.WriteHeader(tar, "big.vmdk", size, DateTimeOffset.UnixEpoch);

        tar.Position = 0;
        Assert.Equal(size, Assert.Single(TarArchive.ReadEntries(tar)).Size);
    }

    [Fact]
    public void A_file_that_is_not_a_tar_is_refused()
    {
        using var junk = new MemoryStream(Enumerable.Range(0, 2048).Select(i => (byte)(i * 7 + 1)).ToArray());
        Assert.Throws<FormatException>(() => TarArchive.ReadEntries(junk));
    }

    // ---- Reading descriptors ----

    private string WriteOvf(string xml, params string[] files)
    {
        var ovf = Path.Combine(_folder, "machine.ovf");
        File.WriteAllText(ovf, xml);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(_folder, file), new byte[512]);
        return ovf;
    }

    private const string VmwareOvf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Envelope vmw:buildId="build-1" xmlns="http://schemas.dmtf.org/ovf/envelope/1" xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                  xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                  xmlns:vmw="http://www.vmware.com/schema/ovf">
          <References><File ovf:href="Win10-disk1.vmdk" ovf:id="file1" ovf:size="512"/></References>
          <DiskSection><Info>disks</Info>
            <Disk ovf:capacity="60" ovf:capacityAllocationUnits="byte * 2^30" ovf:diskId="vmdisk1" ovf:fileRef="file1"
                  ovf:format="http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized"/>
          </DiskSection>
          <VirtualSystem ovf:id="Win10">
            <Info>A virtual machine</Info>
            <Name>Poste Windows</Name>
            <OperatingSystemSection ovf:id="1" vmw:osType="windows9_64Guest"><Info>os</Info></OperatingSystemSection>
            <VirtualHardwareSection>
              <Info>hw</Info>
              <Item><rasd:AllocationUnits>hertz * 10^6</rasd:AllocationUnits><rasd:InstanceID>1</rasd:InstanceID>
                <rasd:ResourceType>3</rasd:ResourceType><rasd:VirtualQuantity>4</rasd:VirtualQuantity>
                <vmw:CoresPerSocket ovf:required="false">2</vmw:CoresPerSocket></Item>
              <Item><rasd:AllocationUnits>byte * 2^20</rasd:AllocationUnits><rasd:InstanceID>2</rasd:InstanceID>
                <rasd:ResourceType>4</rasd:ResourceType><rasd:VirtualQuantity>8192</rasd:VirtualQuantity></Item>
              <Item><rasd:InstanceID>3</rasd:InstanceID><rasd:ResourceSubType>lsilogicsas</rasd:ResourceSubType>
                <rasd:ResourceType>6</rasd:ResourceType></Item>
              <Item><rasd:AddressOnParent>0</rasd:AddressOnParent><rasd:HostResource>ovf:/disk/vmdisk1</rasd:HostResource>
                <rasd:InstanceID>4</rasd:InstanceID><rasd:Parent>3</rasd:Parent><rasd:ResourceType>17</rasd:ResourceType></Item>
              <Item><rasd:Address>00:50:56:aa:bb:cc</rasd:Address><rasd:InstanceID>5</rasd:InstanceID>
                <rasd:ResourceSubType>E1000e</rasd:ResourceSubType><rasd:ResourceType>10</rasd:ResourceType></Item>
              <vmw:Config ovf:required="false" vmw:key="firmware" vmw:value="efi"/>
            </VirtualHardwareSection>
          </VirtualSystem>
        </Envelope>
        """;

    [Fact]
    public void A_VMware_export_keeps_its_hardware()
    {
        var plan = OvfReader.Read(WriteOvf(VmwareOvf, "Win10-disk1.vmdk"));

        Assert.Null(plan.Blocker);
        Assert.Equal(ImportSource.Ovf, plan.Source);
        Assert.Equal("Poste Windows", plan.Name);
        Assert.Equal("windows-10", plan.GuestOs);
        Assert.Equal(FirmwareType.Uefi, plan.Firmware);
        Assert.Equal(4, plan.CpuCount);
        Assert.Equal(2, plan.CoresPerSocket);
        Assert.Equal(8192, plan.MemoryMb);
        var disk = Assert.Single(plan.Disks);
        Assert.Equal(DiskBus.LsiSas, disk.Bus);
        Assert.Equal(Path.Combine(_folder, "Win10-disk1.vmdk"), disk.SourcePath);
        Assert.Null(disk.QemuSource);
        var adapter = Assert.Single(plan.Adapters);
        Assert.Equal(NicModel.E1000e, adapter.Model);
        Assert.Equal("00:50:56:aa:bb:cc", adapter.MacAddress);
    }

    [Fact]
    public void A_VirtualBox_export_is_read_from_its_own_words()
    {
        var xml = """
            <?xml version="1.0"?>
            <Envelope ovf:version="2.0" xmlns="http://schemas.dmtf.org/ovf/envelope/2" xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/2"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                      xmlns:vbox="http://www.virtualbox.org/ovf/machine">
              <References><File ovf:id="file1" ovf:href="Ubuntu-disk001.vmdk"/></References>
              <DiskSection><Info>disks</Info><Disk ovf:capacity="26843545600" ovf:diskId="vmdisk1" ovf:fileRef="file1"
                ovf:format="http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized"/></DiskSection>
              <VirtualSystem ovf:id="Ubuntu">
                <Info>vm</Info>
                <OperatingSystemSection ovf:id="94"><Info>os</Info><Description>Ubuntu (64-bit)</Description>
                  <vbox:OSType ovf:required="false">Ubuntu_64</vbox:OSType></OperatingSystemSection>
                <VirtualHardwareSection>
                  <Info>hw</Info>
                  <Item><rasd:InstanceID>1</rasd:InstanceID><rasd:ResourceType>3</rasd:ResourceType><rasd:VirtualQuantity>2</rasd:VirtualQuantity></Item>
                  <Item><rasd:AllocationUnits>MegaBytes</rasd:AllocationUnits><rasd:InstanceID>2</rasd:InstanceID>
                    <rasd:ResourceType>4</rasd:ResourceType><rasd:VirtualQuantity>4096</rasd:VirtualQuantity></Item>
                  <Item><rasd:InstanceID>5</rasd:InstanceID><rasd:ResourceSubType>AHCI</rasd:ResourceSubType><rasd:ResourceType>20</rasd:ResourceType></Item>
                  <Item><rasd:AddressOnParent>0</rasd:AddressOnParent><rasd:HostResource>/disk/vmdisk1</rasd:HostResource>
                    <rasd:InstanceID>8</rasd:InstanceID><rasd:Parent>5</rasd:Parent><rasd:ResourceType>17</rasd:ResourceType></Item>
                  <Item><rasd:InstanceID>9</rasd:InstanceID><rasd:ResourceSubType>E1000</rasd:ResourceSubType><rasd:ResourceType>10</rasd:ResourceType></Item>
                </VirtualHardwareSection>
                <vbox:Machine ovf:required="false"><Hardware><Firmware type="EFI"/></Hardware></vbox:Machine>
              </VirtualSystem>
            </Envelope>
            """;

        var plan = OvfReader.Read(WriteOvf(xml, "Ubuntu-disk001.vmdk"));

        Assert.Null(plan.Blocker);
        Assert.Equal("Ubuntu", plan.Name);
        Assert.Equal("ubuntu", plan.GuestOs);
        Assert.Equal(FirmwareType.Uefi, plan.Firmware);
        Assert.Equal(4096, plan.MemoryMb);
        Assert.Equal(DiskBus.Sata, Assert.Single(plan.Disks).Bus);
        Assert.Equal(NicModel.E1000, Assert.Single(plan.Adapters).Model);
    }

    [Fact]
    public void A_missing_disk_is_a_blocker()
    {
        var plan = OvfReader.Read(WriteOvf(VmwareOvf));
        Assert.Contains("Win10-disk1.vmdk", plan.Blocker);
    }

    [Theory]
    [InlineData("byte * 2^20", 1)]
    [InlineData("byte * 2^30", 1024)]
    [InlineData("MegaBytes", 1)]
    [InlineData("GigaBytes", 1024)]
    public void Memory_units_are_understood(string units, double megabytes)
    {
        var ovf = VmwareOvf.Replace("<rasd:AllocationUnits>byte * 2^20</rasd:AllocationUnits>",
            $"<rasd:AllocationUnits>{units}</rasd:AllocationUnits>", StringComparison.Ordinal)
            .Replace("<rasd:VirtualQuantity>8192</rasd:VirtualQuantity>", "<rasd:VirtualQuantity>64</rasd:VirtualQuantity>", StringComparison.Ordinal);

        Assert.Equal((int)(64 * megabytes), OvfReader.Read(WriteOvf(ovf, "Win10-disk1.vmdk")).MemoryMb);
    }

    // ---- Writing ----

    private static VmConfig Ubuntu(string folder) => new()
    {
        Name = "Ubuntu de test",
        FolderPath = folder,
        GuestOs = "ubuntu",
        Architecture = GuestArchitecture.X86_64,
        Firmware = FirmwareType.Uefi,
        CpuCount = 4,
        CoresPerSocket = 2,
        MemoryMb = 6144,
        Disks = { new DiskConfig { Id = "disk0", FileName = "disk0.qcow2", Bus = DiskBus.VirtioBlk, BootIndex = 0 } },
        NetworkAdapters = { new NetworkAdapterConfig { Id = "net0", Mode = NetworkMode.Nat, Model = NicModel.VirtioNet, MacAddress = "52:54:00:12:34:56" } }
    };

    [Fact]
    public void VirtIO_hardware_is_exported_as_what_every_hypervisor_has()
    {
        var config = Ubuntu(_folder);
        var document = OvfWriter.Build(config, new[] { new ExportedDisk("u-disk1.vmdk", 1000, 1L << 30, OvfWriter.BusFor(DiskBus.VirtioBlk)) });
        var ovf = document.ToString();
        var cimHardware = document.Descendants().First(e => e.Name.LocalName == "VirtualHardwareSection").ToString();

        Assert.Contains("vmware.sata.ahci", ovf);
        Assert.Contains("<rasd:ResourceSubType>E1000</rasd:ResourceSubType>", ovf);
        Assert.Contains("vmw:osType=\"ubuntu64Guest\"", ovf);
        Assert.Contains("vmw:value=\"efi\"", ovf);
        Assert.Contains("<vbox:OSType ovf:required=\"false\">Ubuntu_64</vbox:OSType>", ovf);
        Assert.DoesNotContain("virtio", cimHardware, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VirtualBox_gets_its_own_configuration_with_the_firmware()
    {
        // VirtualBox reads the firmware nowhere else: without it a UEFI Debian was imported with a BIOS
        // and stopped on "No bootable medium found!".
        var document = OvfWriter.Build(Ubuntu(_folder), new[]
        {
            new ExportedDisk("u-disk1.vmdk", 1000, 1L << 30, ExportBus.Sata),
            new ExportedDisk("u-disk2.vmdk", 1000, 1L << 30, ExportBus.Sata)
        });
        XElement One(string local) => document.Descendants().Single(e => e.Name.LocalName == local);

        var machine = One("Machine");
        Assert.Equal("Ubuntu_64", (string?)machine.Attribute("OSType"));
        Assert.Equal("EFI", (string?)One("Firmware").Attribute("type"));
        Assert.Equal("6144", (string?)One("Memory").Attribute("RAMSize"));
        Assert.Equal("virtio", (string?)One("Adapter").Attribute("type"));
        Assert.Equal("525400123456", (string?)One("Adapter").Attribute("MACAddress"));

        // Each attached image is a disk of the DiskSection, by the uuid VirtualBox looks it up with.
        var controller = One("StorageController");
        Assert.Equal("AHCI", (string?)controller.Attribute("type"));
        Assert.Equal("2", (string?)controller.Attribute("PortCount"));
        var attached = controller.Descendants().Where(e => e.Name.LocalName == "Image").Select(e => (string)e.Attribute("uuid")!).ToList();
        var declared = document.Descendants().Where(e => e.Name.LocalName == "Disk")
            .Select(d => "{" + d.Attributes().Single(a => a.Name.LocalName == "uuid").Value + "}").ToList();
        Assert.Equal(declared, attached);
    }

    [Theory]
    [InlineData("Debian (importée)", "Debian (importee)")]
    [InlineData("Poste de développement", "Poste de developpement")]
    [InlineData("Ubuntu 26.04", "Ubuntu 26.04")]
    [InlineData("Test: 日本", "Test_ __")]
    public void Machine_folders_are_ASCII_since_qemu_system_cannot_open_anything_else(string name, string folder) =>
        Assert.Equal(folder, VmLibrary.SanitizeFolderName(name));

    [Fact]
    public void An_accented_path_reaches_QEMU_as_its_short_name()
    {
        var folder = Path.Combine(_folder, "dossier accentué");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "firmware-vars.fd");
        File.WriteAllBytes(file, new byte[16]);

        var readable = QemuArgumentBuilder.QemuReadablePath(file);

        // A volume without 8.3 names keeps the path: nothing better exists there.
        if (readable != file)
        {
            Assert.True(readable.All(char.IsAscii));
            Assert.True(File.Exists(readable));
        }
        Assert.Equal(@"C:\qemu\Debian\disk0.qcow2", QemuArgumentBuilder.QemuReadablePath(@"C:\qemu\Debian\disk0.qcow2"));

        // A file QEMU is about to create: its folder is shortened, its own name kept.
        var overlay = QemuArgumentBuilder.QemuReadablePath(Path.Combine(folder, "disk0-Avant.qcow2"));
        if (overlay != Path.Combine(folder, "disk0-Avant.qcow2"))
        {
            Assert.True(overlay.All(char.IsAscii));
            Assert.EndsWith(@"\disk0-Avant.qcow2", overlay);
            Assert.True(Directory.Exists(Path.GetDirectoryName(overlay)));
        }
    }

    [Fact]
    public void A_Windows_guest_gets_the_e1000e_it_has_a_driver_for()
    {
        Assert.Equal("E1000e", OvfWriter.NicSubType(NicModel.VirtioNet, "Windows"));
        Assert.Equal("E1000", OvfWriter.NicSubType(NicModel.VirtioNet, "Linux"));
        Assert.Equal("VmxNet3", OvfWriter.NicSubType(NicModel.Vmxnet3, "Windows"));
    }

    [Theory]
    [InlineData("Poste de développement", "Poste-de-developpement")]
    [InlineData("Ubuntu 26.04", "Ubuntu-26.04")]
    [InlineData("***", "machine")]
    public void Package_file_names_are_plain_ASCII(string machine, string expected) =>
        Assert.Equal(expected, OvaExporter.PackageBaseName(machine));

    [Fact]
    [Trait("Category", "Integration")]
    public async Task An_exported_machine_imports_back_with_the_same_disk()
    {
        if (_images is null) return;

        // A recognisable pattern at both ends, so a disk read at the wrong offset in the package shows.
        var raw = new byte[8 * 1024 * 1024];
        for (var i = 0; i < raw.Length; i += 4096)
            BitConverter.GetBytes(i).CopyTo(raw, i);
        var rawPath = Path.Combine(_folder, "pattern.raw");
        await File.WriteAllBytesAsync(rawPath, raw);
        var config = Ubuntu(_folder);
        await _images.ConvertAsync(rawPath, Path.Combine(_folder, "disk0.qcow2"), DiskFormat.Qcow2);
        config.Disks[0].CapacityBytes = raw.Length;

        var ova = Path.Combine(_folder, "export.ova");
        var reports = new List<double>();
        await OvaExporter.ExportAsync(config, ova, _images, new SyncProgress(reports.Add));

        Assert.False(Directory.Exists(ova + ".parts"));
        Assert.Equal(1d, reports[^1]);

        // Descriptor first, manifest second, and the manifest right about every file.
        var entries = TarArchive.ReadEntries(ova);
        Assert.Equal(new[] { "Ubuntu-de-test.ovf", "Ubuntu-de-test.mf", "Ubuntu-de-test-disk1.vmdk" }, entries.Select(e => e.Name));
        var bytes = await File.ReadAllBytesAsync(ova);
        var manifest = Encoding.ASCII.GetString(bytes, (int)entries[1].DataOffset, (int)entries[1].Size);
        foreach (var entry in new[] { entries[0], entries[2] })
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan((int)entry.DataOffset, (int)entry.Size))).ToLowerInvariant();
            Assert.Contains($"SHA256({entry.Name})= {hash}\n", manifest);
        }

        var plan = await VmImporter.ReadAsync(ova, _images);
        Assert.Null(plan.Blocker);
        Assert.Equal("Ubuntu de test", plan.Name);
        Assert.Equal("ubuntu", plan.GuestOs);
        Assert.Equal(FirmwareType.Uefi, plan.Firmware);
        Assert.Equal((4, 2, 6144), (plan.CpuCount, plan.CoresPerSocket, plan.MemoryMb));
        var disk = Assert.Single(plan.Disks);
        Assert.Equal(raw.Length, disk.CapacityBytes);
        Assert.Equal("vmdk", disk.Format);
        Assert.Equal("Ubuntu-de-test-disk1.vmdk", disk.PackageEntry);
        Assert.Equal("52:54:00:12:34:56", Assert.Single(plan.Adapters).MacAddress);

        // Read in place from the package, as the import does.
        var back = Path.Combine(_folder, "back.raw");
        await _images.ConvertAsync(disk.ImageSpec, back, DiskFormat.Raw);
        Assert.Equal(raw, await File.ReadAllBytesAsync(back));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_cancelled_export_leaves_nothing()
    {
        if (_images is null) return;

        var config = Ubuntu(_folder);
        await _images.CreateAsync(Path.Combine(_folder, "disk0.qcow2"), DiskFormat.Qcow2, 64L * 1024 * 1024, backingFile: null);
        var ova = Path.Combine(_folder, "cancelled.ova");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OvaExporter.ExportAsync(config, ova, _images, cancellationToken: new CancellationToken(canceled: true)));

        Assert.False(File.Exists(ova));
        Assert.False(Directory.Exists(ova + ".parts"));
    }

    /// <summary>Reports at once, on the reporting thread: Progress&lt;T&gt; would post them after the test ends.</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SyncProgress(Action<double> report) => _report = report;
        public void Report(double value)
        {
            lock (this)
                _report(value);
        }
    }
}
