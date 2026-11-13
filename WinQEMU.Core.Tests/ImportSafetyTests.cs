using System.Buffers.Binary;
using System.Text;
using WinQEMU.Core.Import;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Images and packages from elsewhere: what they name must not make WinQEMU read files of this PC into a guest, nor sign
/// in to another computer, nor hang (ImportSafety, TarArchive).
/// </summary>
public sealed class ImportSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-import-" + Guid.NewGuid().ToString("N"));

    public ImportSafetyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    /// <summary>A qcow2 header (version 3) naming <paramref name="backing"/>, with the given incompatible features.</summary>
    private string Qcow2(string name, string? backing = null, ulong incompatible = 0)
    {
        var bytes = new byte[4096];
        "QFI"u8.CopyTo(bytes);
        bytes[3] = 0xFB;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(72), incompatible);
        if (backing is not null)
        {
            var text = Encoding.UTF8.GetBytes(backing);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), 512);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)text.Length);
            text.CopyTo(bytes, 512);
        }
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Descriptor(string name, string extents, string? parent = null)
    {
        var text = "# Disk DescriptorFile\nversion=1\nCID=fffffffe\nparentCID=ffffffff\ncreateType=\"monolithicFlat\"\n"
                   + (parent is null ? string.Empty : $"parentFileNameHint=\"{parent}\"\n")
                   + "\n# Extent description\n" + extents + "\n";
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static ImportedDisk Disk(string path, TarEntry? window = null) => new() { SourcePath = path, PackageWindow = window };

    [Fact]
    public void A_plain_image_or_one_whose_chain_stays_in_its_folder_is_accepted()
    {
        Qcow2("base.qcow2");
        Assert.Null(ImportSafety.WhyUnsafe(Disk(Qcow2("top.qcow2", "base.qcow2"))));
        // VMware's snapshot deltas: a parent next to the delta.
        Descriptor("parent.vmdk", "RW 2048 FLAT \"parent-flat.vmdk\" 0");
        Assert.Null(ImportSafety.WhyUnsafe(Disk(Descriptor("delta.vmdk", "RW 2048 FLAT \"delta-flat.vmdk\" 0", "parent.vmdk"))));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Documents\secrets.kdbx")]
    [InlineData(@"..\outside.bin")]
    [InlineData(@"\\203.0.113.9\s\d.img")]
    [InlineData("json:{\"driver\":\"raw\"}")]
    public void A_qcow2_whose_backing_file_lies_elsewhere_is_refused(string backing)
    {
        Assert.NotNull(ImportSafety.WhyUnsafe(Disk(Qcow2("evil.qcow2", backing))));
    }

    [Fact]
    public void A_qcow2_with_an_external_data_file_is_refused()
    {
        Assert.NotNull(ImportSafety.WhyUnsafe(Disk(Qcow2("data.qcow2", incompatible: 0b100))));
    }

    [Fact]
    public void A_backing_file_that_names_one_elsewhere_in_turn_is_refused()
    {
        Qcow2("middle.qcow2", @"C:\Windows\win.ini");
        Assert.NotNull(ImportSafety.WhyUnsafe(Disk(Qcow2("top.qcow2", "middle.qcow2"))));
    }

    [Theory]
    [InlineData("RW 2048 FLAT \"C:\\\\Users\\\\someone\\\\file\" 0")]
    [InlineData("RW 2048 FLAT \"\\\\\\\\attacker\\\\s\\\\x\" 0")]
    [InlineData("RW 2048 FLAT \"../x-flat.vmdk\" 0")]
    public void A_vmdk_descriptor_whose_extent_lies_elsewhere_is_refused(string extent)
    {
        Assert.NotNull(ImportSafety.WhyUnsafe(Disk(Descriptor("evil.vmdk", extent.Replace("\\\\", "\\")))));
    }

    [Fact]
    public void A_text_descriptor_or_a_parent_inside_a_package_is_refused()
    {
        var descriptor = Descriptor("in-package.vmdk", "RW 2048 FLAT \"disk-flat.vmdk\" 0");
        var window = new TarEntry("disk1.vmdk", 0, new FileInfo(descriptor).Length);
        Assert.NotNull(ImportSafety.WhyUnsafe(Disk(descriptor, window)));

        var top = Qcow2("packaged.qcow2", "base.qcow2");
        Assert.NotNull(ImportSafety.WhyUnsafe(Disk(top, new TarEntry("disk1.qcow2", 0, 4096))));
    }

    [Theory]
    [InlineData(@"\\host\share\disk.vmdk", true)]
    [InlineData(@"//host/share/disk.vmdk", true)]
    [InlineData(@"\\.\PhysicalDrive0", true)]
    [InlineData(@"\\?\C:\VMs\disk.vmdk", false)]
    [InlineData(@"C:\VMs\disk.vmdk", false)]
    public void Network_and_device_paths_are_told_from_local_ones(string path, bool remote)
    {
        Assert.Equal(remote, ImportSafety.IsRemote(path));
    }

    [Fact]
    public void A_network_path_is_opened_only_inside_the_folder_the_user_chose()
    {
        Assert.True(ImportSafety.MayOpen(@"\\nas\vms\Debian\disk.vmdk", @"\\nas\vms\Debian\Debian.vmx"));
        Assert.False(ImportSafety.MayOpen(@"\\attacker\s\disk.vmdk", @"C:\Downloads\Debian\Debian.vmx"));
        Assert.True(ImportSafety.MayOpen(@"D:\Other\disk.vmdk", @"C:\Downloads\Debian\Debian.vmx"));
    }

    /// <summary>A tar header with a valid checksum, of the given type and size field.</summary>
    private static byte[] TarHeader(string name, char type, byte[] sizeField)
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        sizeField.CopyTo(header, 124);
        header[156] = (byte)type;
        for (var i = 148; i < 156; i++)
            header[i] = (byte)' ';
        var sum = header.Sum(b => (long)b);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        return header;
    }

    // A negative base-256 size sent the next header back onto this one: the loop never ended, on the UI thread.
    [Fact]
    public void A_negative_size_in_a_package_is_refused_not_looped_on()
    {
        var negative = new byte[] { 0x80, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFC, 0x00 };
        var stream = new MemoryStream();
        stream.Write(TarHeader("disk.vmdk", '0', negative));
        stream.Write(new byte[4096]);
        stream.Position = 0;
        Assert.Throws<FormatException>(() => TarArchive.ReadEntries(stream));
    }

    // Its buffer could not be allocated, with an exception the import did not catch: WinQEMU died.
    [Fact]
    public void A_descriptor_larger_than_its_package_is_refused()
    {
        var huge = new byte[] { 0x80, 0, 0, 0, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var ova = Path.Combine(_folder, "evil.ova");
        using (var file = File.Create(ova))
        {
            file.Write(TarHeader("machine.ovf", '0', huge));
            file.Write(new byte[4096]);
        }
        Assert.Throws<FormatException>(() => OvfReader.Read(ova));
    }
}
