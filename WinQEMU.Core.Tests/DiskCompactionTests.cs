using System.Diagnostics;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>Compaction on a real qcow2 image with the real qemu-img and qemu-io (skipped without QEMU).</summary>
[Trait("Category", "Integration")]
public sealed class DiskCompactionTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-compact-" + Guid.NewGuid().ToString("N"));
    private QemuImgService? _images;
    private string? _qemuIo;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        if ((await QemuLocator.DetectAsync()).Installation is { } installation)
        {
            _images = new QemuImgService(installation);
            var io = Path.Combine(Path.GetDirectoryName(installation.QemuImgPath)!, "qemu-io.exe");
            _qemuIo = File.Exists(io) ? io : null;
        }
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_folder, true);
        return Task.CompletedTask;
    }

    private void Io(string image, string command)
    {
        using var process = Process.Start(new ProcessStartInfo(_qemuIo!) { ArgumentList = { "-c", command, image }, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Clusters_written_with_zeros_are_given_back_and_the_data_stays()
    {
        if (_images is null || _qemuIo is null)
            return;
        var image = Path.Combine(_folder, "disk0.qcow2");
        await _images.CreateAsync(image, DiskFormat.Qcow2, 64L << 20, backingFile: null);
        Io(image, "write -P 0x5a 0 8M");
        // What a guest leaves after deleting files and trimming, or zeroing free space: allocated zeros.
        Io(image, "write -P 0 8M 32M");
        var reference = Path.Combine(_folder, "reference.qcow2");
        File.Copy(image, reference);

        var result = await DiskCompaction.CompactAsync(_images, image);

        Assert.True(result.BytesAfter < result.BytesBefore / 3, $"{result.BytesBefore} -> {result.BytesAfter}");
        using var compare = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(_qemuIo)!, "qemu-img.exe"))
            { ArgumentList = { "compare", reference, image }, UseShellExecute = false, RedirectStandardOutput = true })!;
        compare.StandardOutput.ReadToEnd();
        compare.WaitForExit();
        Assert.Equal(0, compare.ExitCode);
        Assert.False(File.Exists(image + ".compact"));
    }

    [Fact]
    public async Task A_disk_with_snapshots_an_overlay_or_a_linked_clone_is_refused()
    {
        if (_images is null)
            return;
        var image = Path.Combine(_folder, "disk0.qcow2");
        await _images.CreateAsync(image, DiskFormat.Qcow2, 64L << 20, backingFile: null);
        var config = new VmConfig { FolderPath = _folder };
        var disk = new DiskConfig { Id = "disk0", FileName = "disk0.qcow2", Format = DiskFormat.Qcow2 };
        config.Disks.Add(disk);

        Assert.Null(DiskCompaction.WhyNot(config, disk, await _images.GetInfoAsync(image), _ => false));
        Assert.Contains("clone lié lit", DiskCompaction.WhyNot(config, disk, await _images.GetInfoAsync(image), _ => true));

        await _images.CreateOfflineSnapshotAsync(image, "Avant");
        Assert.Contains("instantané", DiskCompaction.WhyNot(config, disk, await _images.GetInfoAsync(image), _ => false));

        disk.LinkedBaseFileName = "../Original/disk0.qcow2";
        Assert.Contains("clone lié", DiskCompaction.WhyNot(config, disk, await _images.GetInfoAsync(image), _ => false));
        disk.LinkedBaseFileName = string.Empty;
        disk.Format = DiskFormat.Raw;
        Assert.Contains("qcow2", DiskCompaction.WhyNot(config, disk, await _images.GetInfoAsync(image), _ => false));
    }
}
