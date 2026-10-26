using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Growing and converting images, against the real qemu-img. Both were written long before anything
/// could reach them, so nothing had ever confirmed they work.
/// </summary>
[Trait("Category", "Integration")]
public class QemuImgDiskTests : IAsyncLifetime
{
    private const long SmallDisk = 64L * 1024 * 1024;

    private string _folder = string.Empty;
    private QemuImgService? _service;

    public async Task InitializeAsync()
    {
        var detection = await QemuLocator.DetectAsync();
        if (detection.Installation is not null)
            _service = new QemuImgService(detection.Installation);

        _folder = Path.Combine(Path.GetTempPath(), "winqemu-disk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    private QemuImgService? Available => _service;

    private async Task<string> NewImageAsync(DiskFormat format = DiskFormat.Qcow2, long capacity = SmallDisk)
    {
        var path = Path.Combine(_folder, $"disk-{Guid.NewGuid():N}.img");
        await _service!.CreateAsync(path, format, capacity, backingFile: null);
        return path;
    }

    // ---- Creation ----

    [Theory]
    [InlineData(DiskFormat.Qcow2, "qcow2")]
    [InlineData(DiskFormat.Raw, "raw")]
    [InlineData(DiskFormat.Vhdx, "vhdx")]
    public async Task An_image_is_created_in_the_requested_format(DiskFormat format, string reported)
    {
        if (Available is null) return;

        var info = await _service!.GetInfoAsync(await NewImageAsync(format));

        Assert.Equal(reported, info.Format);
        Assert.Equal(SmallDisk, info.VirtualSizeBytes);
    }

    // ---- Growing ----

    [Fact]
    public async Task Growing_an_image_reports_the_new_capacity()
    {
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.GrowAsync(path, SmallDisk * 4);

        Assert.Equal(SmallDisk * 4, (await _service.GetInfoAsync(path)).VirtualSizeBytes);
    }

    [Fact]
    public async Task Growing_does_not_make_the_file_claim_the_whole_capacity()
    {
        // The point of growing a qcow2: the guest sees more room, the host gives up nothing yet.
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.GrowAsync(path, SmallDisk * 16);

        var info = await _service.GetInfoAsync(path);
        Assert.True(info.ActualSizeBytes < SmallDisk, $"file grew to {info.ActualSizeBytes} bytes on disk");
    }

    [Fact]
    public async Task Growing_to_the_same_size_is_refused()
    {
        if (Available is null) return;

        var path = await NewImageAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _service!.GrowAsync(path, SmallDisk));
    }

    [Fact]
    public async Task Growing_keeps_the_snapshots_that_were_already_taken()
    {
        // Enlarging a disk must not be a way to lose the history silently.
        if (Available is null) return;

        var path = await NewImageAsync(DiskFormat.Qcow2);
        await _service!.CreateOfflineSnapshotAsync(path, "avant-agrandissement");
        await _service.GrowAsync(path, SmallDisk * 2);

        var snapshot = Assert.Single((await _service.GetInfoAsync(path)).Snapshots);
        Assert.Equal("avant-agrandissement", snapshot.Name);
    }

    // ---- Converting ----

    [Fact]
    public async Task Converting_produces_the_target_format_at_the_same_capacity()
    {
        if (Available is null) return;

        var source = await NewImageAsync(DiskFormat.Qcow2);
        var destination = Path.Combine(_folder, "converti.raw");

        await _service!.ConvertAsync(source, destination, DiskFormat.Raw);

        var info = await _service.GetInfoAsync(destination);
        Assert.Equal("raw", info.Format);
        Assert.Equal(SmallDisk, info.VirtualSizeBytes);
    }

    [Fact]
    public async Task Converting_leaves_the_source_untouched()
    {
        // The shell attaches the converted copy and leaves the original in place, so this has to hold.
        if (Available is null) return;

        var source = await NewImageAsync(DiskFormat.Qcow2);
        var before = new FileInfo(source).Length;

        await _service!.ConvertAsync(source, Path.Combine(_folder, "copie.raw"), DiskFormat.Raw);

        Assert.True(File.Exists(source));
        Assert.Equal(before, new FileInfo(source).Length);
        Assert.Equal("qcow2", (await _service.GetInfoAsync(source)).Format);
    }

    [Fact]
    public async Task A_round_trip_through_raw_comes_back_to_the_same_capacity()
    {
        if (Available is null) return;

        var original = await NewImageAsync(DiskFormat.Qcow2);
        var raw = Path.Combine(_folder, "etape.raw");
        var back = Path.Combine(_folder, "retour.qcow2");

        await _service!.ConvertAsync(original, raw, DiskFormat.Raw);
        await _service.ConvertAsync(raw, back, DiskFormat.Qcow2);

        var info = await _service.GetInfoAsync(back);
        Assert.Equal("qcow2", info.Format);
        Assert.Equal(SmallDisk, info.VirtualSizeBytes);
    }

    [Fact]
    public async Task Converting_does_not_carry_the_snapshots_across()
    {
        // qemu-img convert copies the current contents, not the history. The shell has to say so rather
        // than let a user convert a disk and quietly lose every snapshot on it.
        if (Available is null) return;

        var source = await NewImageAsync(DiskFormat.Qcow2);
        await _service!.CreateOfflineSnapshotAsync(source, "perdu-a-la-conversion");

        var destination = Path.Combine(_folder, "converti.qcow2");
        await _service.ConvertAsync(source, destination, DiskFormat.Qcow2);

        Assert.Empty((await _service.GetInfoAsync(destination)).Snapshots);
        // Still on the original, which is why keeping it matters.
        Assert.Single((await _service.GetInfoAsync(source)).Snapshots);
    }

    [Fact]
    public async Task A_compressed_conversion_is_still_a_readable_qcow2()
    {
        if (Available is null) return;

        var source = await NewImageAsync(DiskFormat.Qcow2);
        var destination = Path.Combine(_folder, "compresse.qcow2");

        await _service!.ConvertAsync(source, destination, DiskFormat.Qcow2, compress: true);

        var info = await _service.GetInfoAsync(destination);
        Assert.Equal("qcow2", info.Format);
        Assert.Equal(SmallDisk, info.VirtualSizeBytes);
    }

    [Fact]
    public async Task Converting_a_missing_source_reports_a_failure()
    {
        if (Available is null) return;

        await Assert.ThrowsAsync<QemuImgException>(() => _service!.ConvertAsync(
            Path.Combine(_folder, "absent.qcow2"),
            Path.Combine(_folder, "sortie.raw"),
            DiskFormat.Raw));
    }
}
