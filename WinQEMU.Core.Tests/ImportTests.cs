using System.Text;
using WinQEMU.Core.Import;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>Reading .vmx and .vbox files, on texts modelled on what VMware Workstation 17 and VirtualBox 7 write.</summary>
public class ImportReaderTests
{
    private const string VmxPath = @"C:\vms\Windows 10\Windows 10.vmx";
    private const string VboxPath = @"C:\vbox\Ubuntu\Ubuntu.vbox";

    private const string Windows10Vmx = """
        .encoding = "UTF-8"
        config.version = "8"
        displayName = "Windows 10 |22pro|22"
        guestOS = "windows9-64"
        firmware = "efi"
        uefi.secureBoot.enabled = "TRUE"
        memsize = "8192"
        numvcpus = "4"
        cpuid.coresPerSocket = "2"
        nvme0.present = "TRUE"
        nvme0:0.fileName = "Windows 10-000002.vmdk"
        nvme0:0.present = "TRUE"
        sata0.present = "TRUE"
        sata0:1.deviceType = "cdrom-image"
        sata0:1.fileName = "C:\iso\Win10.iso"
        sata0:1.present = "TRUE"
        ethernet0.present = "TRUE"
        ethernet0.connectionType = "nat"
        ethernet0.virtualDev = "vmxnet3"
        ethernet0.addressType = "generated"
        ethernet0.generatedAddress = "00:0C:29:AB:CD:EF"
        """;

    [Fact]
    public void A_vmware_windows_machine_keeps_its_hardware()
    {
        var plan = VmxReader.Parse(Windows10Vmx, VmxPath);

        Assert.Equal("Windows 10 \"pro\"", plan.Name);
        Assert.Equal("windows-10", plan.GuestOs);
        Assert.Equal(FirmwareType.Uefi, plan.Firmware);
        Assert.Equal(8192, plan.MemoryMb);
        Assert.Equal(4, plan.CpuCount);
        Assert.Equal(2, plan.CoresPerSocket);

        var disk = Assert.Single(plan.Disks);
        Assert.Equal(@"C:\vms\Windows 10\Windows 10-000002.vmdk", disk.SourcePath);
        Assert.Equal(DiskBus.Nvme, disk.Bus);

        var adapter = Assert.Single(plan.Adapters);
        Assert.Equal(NicModel.Vmxnet3, adapter.Model);
        Assert.Equal("00:0c:29:ab:cd:ef", adapter.MacAddress);

        // Secure Boot cannot follow under WHPX, and the user is told.
        Assert.Contains(plan.Notes, n => n.Contains("démarrage sécurisé", StringComparison.Ordinal));
        Assert.Null(plan.Blocker);
    }

    [Theory]
    [InlineData("lsisas1068", DiskBus.LsiSas, false)]
    [InlineData("pvscsi", DiskBus.Pvscsi, false)]
    [InlineData("lsilogic", DiskBus.Sata, true)]
    [InlineData("buslogic", DiskBus.Sata, true)]
    public void Scsi_controllers_keep_their_model_where_qemu_has_it(string model, DiskBus expected, bool noted)
    {
        var plan = VmxReader.Parse($"""
            guestOS = "windows2019srv-64"
            scsi0.present = "TRUE"
            scsi0.virtualDev = "{model}"
            scsi0:0.fileName = "disk.vmdk"
            scsi0:0.present = "TRUE"
            """, VmxPath);

        Assert.Equal(expected, Assert.Single(plan.Disks).Bus);
        Assert.Equal(noted, plan.Notes.Any(n => n.Contains("INACCESSIBLE_BOOT_DEVICE", StringComparison.Ordinal)));
    }

    [Fact]
    public void Absent_devices_and_optical_drives_are_not_disks_and_nvme_boots_first()
    {
        var plan = VmxReader.Parse("""
            sata0.present = "TRUE"
            sata0:0.fileName = "data.vmdk"
            sata0:0.present = "TRUE"
            sata0:2.fileName = "removed.vmdk"
            sata0:2.present = "FALSE"
            sata0:1.fileName = "auto detect"
            sata0:1.deviceType = "atapi-cdrom"
            nvme0.present = "TRUE"
            nvme0:0.fileName = "system.vmdk"
            nvme0:0.present = "TRUE"
            """, VmxPath);

        Assert.Equal(new[] { "system.vmdk", "data.vmdk" }, plan.Disks.Select(d => Path.GetFileName(d.SourcePath)));
        Assert.Equal(string.Empty, plan.IsoPath);
    }

    [Fact]
    public void A_bridged_adapter_falls_back_to_nat_and_says_so()
    {
        var plan = VmxReader.Parse("""
            ethernet0.present = "TRUE"
            ethernet0.connectionType = "bridged"
            ethernet0.addressType = "static"
            ethernet0.address = "00:50:56:01:02:03"
            """, VmxPath);

        var adapter = Assert.Single(plan.Adapters);
        Assert.Equal(NetworkMode.Nat, adapter.Mode);
        Assert.Equal(NicModel.E1000, adapter.Model);
        Assert.Equal("00:50:56:01:02:03", adapter.MacAddress);
        Assert.Contains(plan.Notes, n => n.Contains("bridged", StringComparison.Ordinal));
    }

    [Fact]
    public void An_encrypted_machine_is_refused()
    {
        var plan = VmxReader.Parse("""
            encryption.keySafe = "vmware:key/list/(pair/(phrase/xxx))"
            """, VmxPath);

        Assert.NotNull(plan.Blocker);
    }

    [Theory]
    [InlineData("windows11-64", "windows-11")]
    [InlineData("windows9-64", "windows-10")]
    [InlineData("windows2019srvNext-64", "windows-server")]
    [InlineData("windows7-64", "windows-7")]
    [InlineData("ubuntu-64", "ubuntu")]
    [InlineData("debian12-64", "debian")]
    [InlineData("rhel9-64", "fedora")]
    [InlineData("other6xlinux-64", "linux")]
    [InlineData("freebsd-64", "other")]
    public void Vmware_guest_identifiers_map_to_profiles(string guestOs, string expected) =>
        Assert.Equal(expected, GuestOsMapping.FromVmware(guestOs));

    [Theory]
    [InlineData("Windows11_64", "windows-11")]
    [InlineData("Windows10_64", "windows-10")]
    [InlineData("Windows2022_64", "windows-server")]
    [InlineData("Windows81_64", "windows-7")]
    [InlineData("Ubuntu24_LTS_64", "ubuntu")]
    [InlineData("Debian_64", "debian")]
    [InlineData("Oracle_64", "fedora")]
    [InlineData("ArchLinux_64", "linux")]
    [InlineData("Solaris11_64", "other")]
    public void VirtualBox_os_types_map_to_profiles(string osType, string expected) =>
        Assert.Equal(expected, GuestOsMapping.FromVirtualBox(osType));

    private const string UbuntuVbox = """
        <?xml version="1.0"?>
        <VirtualBox xmlns="http://www.virtualbox.org/" version="1.19-windows">
          <Machine uuid="{11111111-1111-1111-1111-111111111111}" name="Ubuntu 24.04" OSType="Ubuntu_64">
            <MediaRegistry>
              <HardDisks>
                <HardDisk uuid="{22222222-2222-2222-2222-222222222222}" location="Ubuntu.vdi" format="VDI" type="Normal"/>
              </HardDisks>
              <DVDImages>
                <Image uuid="{33333333-3333-3333-3333-333333333333}" location="C:/iso/ubuntu.iso"/>
              </DVDImages>
            </MediaRegistry>
            <Hardware>
              <CPU count="3"/>
              <Memory RAMSize="6144"/>
              <Firmware type="EFI"/>
              <Network>
                <Adapter slot="0" enabled="true" MACAddress="080027A1B2C3" type="82540EM">
                  <NAT/>
                </Adapter>
                <Adapter slot="1" enabled="false" MACAddress="080027000000" type="82540EM"/>
              </Network>
            </Hardware>
            <StorageControllers>
              <StorageController name="SATA" type="AHCI" PortCount="2">
                <AttachedDevice type="HardDisk" port="0" device="0">
                  <Image uuid="{22222222-2222-2222-2222-222222222222}"/>
                </AttachedDevice>
                <AttachedDevice passthrough="false" type="DVD" port="1" device="0">
                  <Image uuid="{33333333-3333-3333-3333-333333333333}"/>
                </AttachedDevice>
              </StorageController>
            </StorageControllers>
          </Machine>
        </VirtualBox>
        """;

    [Fact]
    public void A_virtualbox_machine_keeps_its_hardware()
    {
        var plan = VboxReader.Parse(UbuntuVbox, VboxPath);

        Assert.Equal("Ubuntu 24.04", plan.Name);
        Assert.Equal("ubuntu", plan.GuestOs);
        Assert.Equal(FirmwareType.Uefi, plan.Firmware);
        Assert.Equal(3, plan.CpuCount);
        Assert.Equal(6144, plan.MemoryMb);

        var disk = Assert.Single(plan.Disks);
        Assert.Equal(@"C:\vbox\Ubuntu\Ubuntu.vdi", disk.SourcePath);
        Assert.Equal(DiskBus.Sata, disk.Bus);

        // The disabled second adapter does not come along.
        var adapter = Assert.Single(plan.Adapters);
        Assert.Equal("08:00:27:a1:b2:c3", adapter.MacAddress);
        Assert.Empty(plan.Notes);
        Assert.Null(plan.Blocker);
    }

    [Fact]
    public void A_virtualbox_machine_with_snapshots_is_refused_with_the_way_out()
    {
        var xml = UbuntuVbox
            .Replace("""type="Normal"/>""", """type="Normal"><HardDisk uuid="{44444444-4444-4444-4444-444444444444}" location="Snapshots/{44444444-4444-4444-4444-444444444444}.vdi" format="VDI"/></HardDisk>""")
            // The current state is attached, as VirtualBox does once a snapshot exists.
            .Replace("""<Image uuid="{22222222-2222-2222-2222-222222222222}"/>""", """<Image uuid="{44444444-4444-4444-4444-444444444444}"/>""");

        var plan = VboxReader.Parse(xml, VboxPath);

        Assert.NotNull(plan.Blocker);
        Assert.Contains("instantanés", plan.Blocker);
    }

    [Fact]
    public void A_gpt_disk_is_recognised_by_its_header_signature()
    {
        var start = new byte[1024];
        Assert.False(VmImporter.HasGpt(start));

        Encoding.ASCII.GetBytes("EFI PART").CopyTo(start, 512);
        Assert.True(VmImporter.HasGpt(start));
    }

    // Type GUIDs as gdisk and the Windows installer write them.
    internal const string EfiSystem = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";
    internal const string LinuxFilesystem = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";
    internal const string MicrosoftReserved = "E3C9E316-0B5C-4DB8-817D-F92DF00215AE";
    internal const string BasicData = "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7";

    /// <summary>The first 34 sectors of a disk: protective MBR, GPT header, then one entry per type.</summary>
    internal static byte[] GptStart(params string[] types)
    {
        var start = new byte[34 * 512];
        start[510] = 0x55;
        start[511] = 0xAA;
        start[446 + 4] = 0xEE;
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(start, 512);
        BitConverter.GetBytes(2L).CopyTo(start, 512 + 72);
        BitConverter.GetBytes(128u).CopyTo(start, 512 + 80);
        BitConverter.GetBytes(128u).CopyTo(start, 512 + 84);
        for (var i = 0; i < types.Length; i++)
            new Guid(types[i]).ToByteArray().CopyTo(start, 1024 + i * 128);
        return start;
    }

    private static byte[] MbrStart(params byte[] types)
    {
        var start = new byte[34 * 512];
        start[510] = 0x55;
        start[511] = 0xAA;
        for (var i = 0; i < types.Length; i++)
            start[446 + i * 16 + 4] = types[i];
        return start;
    }

    [Fact]
    public void The_partition_types_tell_a_linux_disk_from_a_windows_one()
    {
        // Ubuntu as installed on a Hyper-V generation 2 machine: an ESP and one ext4 partition.
        Assert.Equal(VmImporter.GuestFamily.Linux, VmImporter.DetectFamily(GptStart(EfiSystem, LinuxFilesystem)));
        Assert.Equal(VmImporter.GuestFamily.Windows, VmImporter.DetectFamily(GptStart(EfiSystem, MicrosoftReserved, BasicData)));
        // Basic data alone is also what a Linux data disk carries: no conclusion.
        Assert.Equal(VmImporter.GuestFamily.Unknown, VmImporter.DetectFamily(GptStart(EfiSystem, BasicData)));

        Assert.Equal(VmImporter.GuestFamily.Linux, VmImporter.DetectFamily(MbrStart(0x83, 0x82)));
        Assert.Equal(VmImporter.GuestFamily.Windows, VmImporter.DetectFamily(MbrStart(0x07, 0x07)));
        Assert.Equal(VmImporter.GuestFamily.Unknown, VmImporter.DetectFamily(new byte[34 * 512]));
    }
}

/// <summary>
/// Importing real images made with qemu-img in the formats of each hypervisor, checking the bytes the
/// imported disk holds. Skipped when QEMU is not installed.
/// </summary>
[Trait("Category", "Integration")]
public class ImportTests : IAsyncLifetime
{
    private const long SmallDisk = 16L * 1024 * 1024;

    private string _root = string.Empty;
    private string _sources = string.Empty;
    private QemuImgService? _images;
    private string _qemuIo = string.Empty;

    public async Task InitializeAsync()
    {
        var installation = (await QemuLocator.DetectAsync()).Installation;
        if (installation is not null)
        {
            _images = new QemuImgService(installation);
            _qemuIo = Path.Combine(Path.GetDirectoryName(installation.QemuImgPath)!, "qemu-io.exe");
        }

        _root = Path.Combine(Path.GetTempPath(), "winqemu-import-" + Guid.NewGuid().ToString("N"));
        _sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(_sources);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private bool Available => _images is not null && File.Exists(_qemuIo);

    private VmLibrary Library => new(Path.Combine(_root, "library"), _images);

    private string Source(string file) => Path.Combine(_sources, file);

    private Task Io(string path, string command) =>
        ProcessRunner.RunAsync(_qemuIo, new[] { "-c", command, path }, CancellationToken.None);

    private async Task<bool> Holds(string path, string pattern, string offset)
    {
        var result = await ProcessRunner.RunAsync(
            _qemuIo, new[] { "-r", "-c", $"read -P {pattern} {offset} 64k", path }, CancellationToken.None);
        return result.Succeeded
               && !(result.StandardOutput + result.StandardError).Contains("verification failed", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_vmware_machine_with_a_snapshot_chain_comes_across_whole()
    {
        if (!Available) return;
        // The base disk, then a delta on it as a VMware snapshot leaves: the .vmx names the delta.
        await _images!.CreateAsync(Source("disk.vmdk"), DiskFormat.Vmdk, SmallDisk);
        await Io(Source("disk.vmdk"), "write -P 0x11 0 64k");
        await _images.CreateAsync(Source("disk-000001.vmdk"), DiskFormat.Vmdk, SmallDisk, "disk.vmdk");
        await Io(Source("disk-000001.vmdk"), "write -P 0x22 64k 64k");
        await File.WriteAllTextAsync(Source("Serveur.vmx"), """
            displayName = "Serveur"
            guestOS = "windows2019srv-64"
            memsize = "4096"
            numvcpus = "2"
            scsi0.present = "TRUE"
            scsi0.virtualDev = "lsisas1068"
            scsi0:0.fileName = "disk-000001.vmdk"
            scsi0:0.present = "TRUE"
            ethernet0.present = "TRUE"
            ethernet0.connectionType = "nat"
            ethernet0.virtualDev = "e1000e"
            ethernet0.addressType = "generated"
            ethernet0.generatedAddress = "00:0c:29:11:22:33"
            """);

        var plan = await VmImporter.ReadAsync(Source("Serveur.vmx"), _images);
        Assert.Null(plan.Blocker);
        Assert.Equal(SmallDisk, plan.Disks[0].CapacityBytes);

        var vm = await Library.ImportAsync(plan, plan.Name);

        var disk = Assert.Single(vm.Disks);
        Assert.Equal(DiskFormat.Qcow2, disk.Format);
        Assert.Equal(DiskBus.LsiSas, disk.Bus);
        var path = vm.ResolvePath(disk.FileName);
        Assert.StartsWith(vm.FolderPath, path);
        Assert.True(await Holds(path, "0x11", "0"));
        Assert.True(await Holds(path, "0x22", "64k"));
        Assert.Null((await _images.GetInfoAsync(path)).BackingFile);

        Assert.Equal("00:0c:29:11:22:33", vm.NetworkAdapters[0].MacAddress);
        Assert.Equal(NicModel.E1000e, vm.NetworkAdapters[0].Model);
        Assert.True(vm.RtcLocalTime);   // Windows keeps its clock in local time.
        Assert.True(File.Exists(vm.DescriptorPath));

        // The source is only read.
        Assert.True(await Holds(Source("disk-000001.vmdk"), "0x22", "64k"));
    }

    [Fact]
    public async Task A_virtualbox_machine_comes_across()
    {
        if (!Available) return;
        await _images!.CreateAsync(Source("Ubuntu.vdi"), DiskFormat.Vdi, SmallDisk);
        await Io(Source("Ubuntu.vdi"), "write -P 0x33 0 64k");
        await File.WriteAllTextAsync(Source("Ubuntu.vbox"), """
            <?xml version="1.0"?>
            <VirtualBox xmlns="http://www.virtualbox.org/" version="1.19-windows">
              <Machine uuid="{11111111-1111-1111-1111-111111111111}" name="Ubuntu" OSType="Ubuntu_64">
                <MediaRegistry><HardDisks>
                  <HardDisk uuid="{22222222-2222-2222-2222-222222222222}" location="Ubuntu.vdi" format="VDI" type="Normal"/>
                </HardDisks></MediaRegistry>
                <Hardware><Memory RAMSize="2048"/></Hardware>
                <StorageControllers>
                  <StorageController name="SATA" type="AHCI">
                    <AttachedDevice type="HardDisk" port="0" device="0"><Image uuid="{22222222-2222-2222-2222-222222222222}"/></AttachedDevice>
                  </StorageController>
                </StorageControllers>
              </Machine>
            </VirtualBox>
            """);

        var plan = await VmImporter.ReadAsync(Source("Ubuntu.vbox"), _images);
        var vm = await Library.ImportAsync(plan, "Ubuntu importée");

        Assert.Equal("ubuntu", vm.GuestOs);
        Assert.False(vm.RtcLocalTime);
        Assert.True(await Holds(vm.ResolvePath(vm.Disks[0].FileName), "0x33", "0"));
    }

    [Theory]
    [InlineData(true, FirmwareType.Uefi)]
    [InlineData(false, FirmwareType.Bios)]
    public async Task A_lone_hyperv_disk_gets_the_firmware_its_partition_table_needs(bool gpt, FirmwareType expected)
    {
        if (!Available) return;
        var raw = new byte[SmallDisk];
        if (gpt)
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(raw, 512);
        await File.WriteAllBytesAsync(Source("disk.img"), raw);
        await _images!.ConvertAsync(Source("disk.img"), Source("Serveur.vhdx"), DiskFormat.Vhdx);

        var plan = await VmImporter.ReadAsync(Source("Serveur.vhdx"), _images);

        Assert.Equal(ImportSource.DiskImage, plan.Source);
        Assert.Equal("Serveur", plan.Name);
        Assert.Equal(expected, plan.Firmware);
        Assert.Null(plan.Blocker);
        Assert.Contains(plan.Notes, n => n.Contains("Hyper-V", StringComparison.Ordinal));

        var vm = await Library.ImportAsync(plan, plan.Name);
        Assert.Equal(expected, vm.Firmware);
        Assert.Equal(DiskBus.Sata, vm.Disks[0].Bus);
    }

    [Fact]
    public async Task A_lone_linux_disk_from_hyperv_gets_virtio()
    {
        if (!Available) return;
        // An Ubuntu 26.04 installed under Hyper-V has only hv_storvsc in its initramfs: on SATA it stopped
        // in emergency mode; on VirtIO, built into its kernel, it booted.
        var raw = new byte[SmallDisk];
        ImportReaderTests.GptStart(ImportReaderTests.EfiSystem, ImportReaderTests.LinuxFilesystem).CopyTo(raw, 0);
        await File.WriteAllBytesAsync(Source("disk.img"), raw);
        await _images!.ConvertAsync(Source("disk.img"), Source("UBUNTU-LTS.vhdx"), DiskFormat.Vhdx);

        var plan = await VmImporter.ReadAsync(Source("UBUNTU-LTS.vhdx"), _images);

        Assert.Equal(FirmwareType.Uefi, plan.Firmware);
        Assert.Equal("linux", plan.GuestOs);
        Assert.Contains(plan.Notes, n => n.Contains("initramfs", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("storahci", StringComparison.Ordinal));

        var vm = await Library.ImportAsync(plan, plan.Name);
        Assert.Equal(DiskBus.VirtioBlk, vm.Disks[0].Bus);
        Assert.Equal(NicModel.VirtioNet, vm.NetworkAdapters[0].Model);
        Assert.Equal("Importée d'une image disque", vm.Description[..27]);
    }

    [Fact]
    public async Task A_vmware_machine_still_open_is_refused()
    {
        if (!Available) return;
        await _images!.CreateAsync(Source("disk.vmdk"), DiskFormat.Vmdk, SmallDisk);
        Directory.CreateDirectory(Source("disk.vmdk.lck"));
        await File.WriteAllTextAsync(Source("Ouverte.vmx"), """
            sata0:0.fileName = "disk.vmdk"
            """);

        var plan = await VmImporter.ReadAsync(Source("Ouverte.vmx"), _images);

        Assert.NotNull(plan.Blocker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.ImportAsync(plan, "Ouverte"));
        Assert.False(Directory.Exists(Path.Combine(_root, "library", "Ouverte")));
    }

    [Fact]
    public async Task A_missing_disk_is_refused_before_anything_is_created()
    {
        if (!Available) return;
        await File.WriteAllTextAsync(Source("Vide.vmx"), """
            sata0:0.fileName = "absent.vmdk"
            """);

        var plan = await VmImporter.ReadAsync(Source("Vide.vmx"), _images!);

        Assert.Contains("absent.vmdk", plan.Blocker);
    }
}
