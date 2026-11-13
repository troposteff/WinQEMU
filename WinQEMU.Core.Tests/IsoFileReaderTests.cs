using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>Images picked in the wizard are read from a property getter: a damaged one must read as "not there", never throw.</summary>
public sealed class IsoFileReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wq-iso-{Guid.NewGuid():N}.iso");

    public void Dispose() => File.Delete(_path);

    private void WriteImage(uint rootLength, Action<byte[]>? alter = null)
    {
        var data = new byte[2048 * 40];
        var pvd = 16 * 2048;
        data[pvd] = 1;
        "CD001"u8.CopyTo(data.AsSpan(pvd + 1));
        BitConverter.GetBytes(20u).CopyTo(data, pvd + 156 + 2);
        BitConverter.GetBytes(rootLength).CopyTo(data, pvd + 156 + 10);
        data[pvd + 156 + 25] = 2;
        alter?.Invoke(data);
        File.WriteAllBytes(_path, data);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]   // wrapped around to one sector, then read past it
    [InlineData(0x7FFFFFFFu)]   // gigabytes asked for
    public void A_forged_root_length_reads_as_absent(uint length)
    {
        WriteImage(length);

        Assert.False(IsoFileReader.Contains(_path, "images/pxeboot/vmlinuz"));
        Assert.Null(IsoFileReader.Read(_path, "sources/lang.ini"));
        Assert.False(LinuxUnattendedSetup.CanInstall("fedora", _path));
    }

    [Fact]
    public void A_record_whose_name_runs_past_its_directory_reads_as_absent()
    {
        WriteImage(2048, data =>
        {
            var record = 20 * 2048 + 2048 - 40;   // last record of the directory, its name longer than what is left
            data[record] = 40;
            data[record + 32] = 200;
        });

        Assert.False(IsoFileReader.Contains(_path, "casper/vmlinuz"));
    }
}
