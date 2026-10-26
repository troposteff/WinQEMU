using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Snapshots, exercised against the real qemu-img on throwaway images.
/// </summary>
/// <remarks>
/// Deliberately not mocked. What matters here is not that we call qemu-img with certain words — it is
/// that the words mean what the snapshots feature assumes: that a name can be created and listed back,
/// that applying one really restores earlier state, that deleting an absent name fails rather than
/// silently succeeding, and that raw images have no snapshot support at all. A stub would confirm the
/// spelling and none of the behaviour.
///
/// Skipped when QEMU is not installed, so the suite stays runnable on a machine without it.
/// </remarks>
[Trait("Category", "Integration")]
public class QemuImgSnapshotTests : IAsyncLifetime
{
    private const long SmallDisk = 64L * 1024 * 1024;

    private string _folder = string.Empty;
    private QemuImgService? _service;

    public async Task InitializeAsync()
    {
        var detection = await QemuLocator.DetectAsync();
        if (detection.Installation is not null)
            _service = new QemuImgService(detection.Installation);

        _folder = Path.Combine(Path.GetTempPath(), "winqemu-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>The service, or null when QEMU is absent and the test should stand down.</summary>
    private QemuImgService? Available => _service;

    private async Task<string> NewImageAsync(DiskFormat format = DiskFormat.Qcow2, long capacity = SmallDisk)
    {
        var path = Path.Combine(_folder, $"disk-{Guid.NewGuid():N}.{(format == DiskFormat.Qcow2 ? "qcow2" : "img")}");
        await _service!.CreateAsync(path, format, capacity, backingFile: null);
        return path;
    }

    [Fact]
    public async Task A_fresh_image_has_no_snapshots()
    {
        if (Available is null) return;

        var info = await _service!.GetInfoAsync(await NewImageAsync());

        Assert.Equal("qcow2", info.Format);
        Assert.Equal(SmallDisk, info.VirtualSizeBytes);
        Assert.Empty(info.Snapshots);
        // qcow2 is sparse, so an empty 64 MiB image occupies far less than its capacity.
        Assert.True(info.ActualSizeBytes < SmallDisk, $"actual size {info.ActualSizeBytes} should be sparse");
    }

    [Fact]
    public async Task A_created_snapshot_is_listed_back_by_name()
    {
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "avant-maj");

        var info = await _service.GetInfoAsync(path);
        var snapshot = Assert.Single(info.Snapshots);

        Assert.Equal("avant-maj", snapshot.Name);
        Assert.NotEqual(string.Empty, snapshot.Id);
        // Disk-only snapshot: no guest memory was saved, which is what the shell reports as "état disque".
        Assert.Equal(0, snapshot.VmStateSizeBytes);
        Assert.True(
            snapshot.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5),
            $"timestamp {snapshot.CreatedAt} should be recent, so the shell can order the list by it");
    }

    [Fact]
    public async Task Several_snapshots_coexist_and_are_all_listed()
    {
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "un");
        await _service.CreateOfflineSnapshotAsync(path, "deux");
        await _service.CreateOfflineSnapshotAsync(path, "trois");

        var info = await _service.GetInfoAsync(path);

        Assert.Equal(new[] { "un", "deux", "trois" }, info.Snapshots.Select(s => s.Name));
    }

    [Fact]
    public async Task Applying_a_snapshot_restores_the_state_it_captured()
    {
        if (Available is null) return;

        // Growing the image is a change the snapshot can be observed to undo; nothing here can write
        // into a guest filesystem, but the disk geometry is part of the captured state.
        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "avant");

        await _service.GrowAsync(path, SmallDisk * 2);
        Assert.Equal(SmallDisk * 2, (await _service.GetInfoAsync(path)).VirtualSizeBytes);

        await _service.ApplyOfflineSnapshotAsync(path, "avant");

        Assert.Equal(SmallDisk, (await _service.GetInfoAsync(path)).VirtualSizeBytes);
    }

    [Fact]
    public async Task Applying_a_snapshot_keeps_it_available_for_next_time()
    {
        // The shell refreshes the list after a restore and must still show the snapshot afterwards.
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "repere");
        await _service.ApplyOfflineSnapshotAsync(path, "repere");

        Assert.Single((await _service.GetInfoAsync(path)).Snapshots);
    }

    [Fact]
    public async Task Deleting_a_snapshot_leaves_the_others_alone()
    {
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "garder");
        await _service.CreateOfflineSnapshotAsync(path, "jeter");

        await _service.DeleteOfflineSnapshotAsync(path, "jeter");

        var remaining = Assert.Single((await _service.GetInfoAsync(path)).Snapshots);
        Assert.Equal("garder", remaining.Name);
    }

    [Fact]
    public async Task Deleting_a_snapshot_leaves_the_disk_itself_usable()
    {
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "jeter");
        await _service.DeleteOfflineSnapshotAsync(path, "jeter");

        var info = await _service.GetInfoAsync(path);

        Assert.Equal(SmallDisk, info.VirtualSizeBytes);
        Assert.Empty(info.Snapshots);
    }

    [Fact]
    public async Task Deleting_an_absent_snapshot_reports_a_failure()
    {
        // The snapshots view-model relies on this: when a snapshot covers only some disks of a machine,
        // it swallows this exception per disk so one missing name cannot abort the whole cleanup.
        if (Available is null) return;

        var path = await NewImageAsync();

        await Assert.ThrowsAsync<QemuImgException>(() => _service!.DeleteOfflineSnapshotAsync(path, "jamais-pris"));
    }

    [Fact]
    public async Task Applying_an_absent_snapshot_reports_a_failure()
    {
        if (Available is null) return;

        var path = await NewImageAsync();

        await Assert.ThrowsAsync<QemuImgException>(() => _service!.ApplyOfflineSnapshotAsync(path, "jamais-pris"));
    }

    [Fact]
    public async Task A_raw_image_cannot_hold_a_snapshot()
    {
        // Why the shell offers snapshots only for qcow2 disks and names the excluded ones.
        if (Available is null) return;

        var path = await NewImageAsync(DiskFormat.Raw);

        await Assert.ThrowsAsync<QemuImgException>(() => _service!.CreateOfflineSnapshotAsync(path, "impossible"));
    }

    [Fact]
    public async Task A_snapshot_name_with_spaces_survives_the_round_trip()
    {
        // The name reaches qemu-img as one argument, so quoting must not split it.
        if (Available is null) return;

        var path = await NewImageAsync();
        await _service!.CreateOfflineSnapshotAsync(path, "avant la mise a jour");

        var snapshot = Assert.Single((await _service.GetInfoAsync(path)).Snapshots);
        Assert.Equal("avant la mise a jour", snapshot.Name);
    }

    [Fact]
    public async Task An_accented_snapshot_name_is_refused_rather_than_silently_corrupted()
    {
        // qemu-img on Windows receives its arguments through the ANSI code page and writes U+FFFD into
        // the image, so the name would come back unrecognisable. Proven by passing correct UTF-8 bytes
        // to qemu-img directly: it still stored the replacement character.
        if (Available is null) return;

        var path = await NewImageAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service!.CreateOfflineSnapshotAsync(path, "avant la mise à jour"));

        Assert.Empty((await _service!.GetInfoAsync(path)).Snapshots);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_snapshot_name_is_refused(string name)
    {
        Assert.NotNull(QemuImgService.ValidateSnapshotName(name));
    }

    [Theory]
    [InlineData("avant-maj")]
    [InlineData("Avant la mise a jour 2")]
    [InlineData("v1.2_final")]
    public void Plain_ascii_names_are_accepted(string name)
    {
        Assert.Null(QemuImgService.ValidateSnapshotName(name));
    }

    [Theory]
    [InlineData("avant-été")]
    [InlineData("à jeter")]
    [InlineData("café")]
    public void Names_qemu_img_would_mangle_are_refused(string name)
    {
        Assert.NotNull(QemuImgService.ValidateSnapshotName(name));
    }

    [Fact]
    public void An_over_long_snapshot_name_is_refused()
    {
        Assert.NotNull(QemuImgService.ValidateSnapshotName(new string('a', 129)));
        Assert.Null(QemuImgService.ValidateSnapshotName(new string('a', 128)));
    }

    [Fact]
    public async Task Snapshots_are_per_image_not_shared()
    {
        // A two-disk machine takes one snapshot per disk under the same name; they must stay independent
        // so a partial snapshot is detectable rather than silently global.
        if (Available is null) return;

        var first = await NewImageAsync();
        var second = await NewImageAsync();

        await _service!.CreateOfflineSnapshotAsync(first, "commun");

        Assert.Single((await _service.GetInfoAsync(first)).Snapshots);
        Assert.Empty((await _service.GetInfoAsync(second)).Snapshots);
    }

    [Fact]
    public async Task Shrinking_a_disk_is_refused_rather_than_truncating_it()
    {
        // Not a snapshot rule, but the same service and the same stake: qemu-img cannot know where the
        // guest filesystem ends, so a shrink would quietly destroy data.
        if (Available is null) return;

        var path = await NewImageAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _service!.GrowAsync(path, SmallDisk / 2));
    }
}
