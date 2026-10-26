using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Live disk snapshots are overlay levels, not internal snapshots, so they never showed in the snapshot
/// list. These cover listing them and undoing them.
/// </summary>
public class LiveSnapshotListingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    private static VmConfig TwoDisks()
    {
        var vm = Fixtures.Vm();
        vm.Disks.Clear();
        vm.Disks.Add(new DiskConfig { Id = "disk0", FileName = "disk0.qcow2" });
        vm.Disks.Add(new DiskConfig { Id = "disk1", FileName = "disk1.qcow2" });
        return vm;
    }

    [Fact]
    public void The_snapshot_name_is_read_back_from_the_overlay_file()
    {
        Assert.Equal("Test 1", DiskConfig.SnapshotNameOf("disk0", "disk0-Test 1.qcow2"));
        Assert.Equal("custom", DiskConfig.SnapshotNameOf("disk0", "custom.qcow2"));
    }

    [Fact]
    public void Each_overlay_level_is_one_snapshot_spanning_the_disks_that_have_it()
    {
        var vm = TwoDisks();
        vm.Disks[0].PushOverlay("disk0-Test 1.qcow2");
        vm.Disks[1].PushOverlay("disk1-Test 1.qcow2");
        vm.Disks[0].PushOverlay("disk0-Test 2.qcow2");

        var minutes = new Dictionary<string, int> { ["disk0-Test 1.qcow2"] = 1, ["disk1-Test 1.qcow2"] = 1, ["disk0-Test 2.qcow2"] = 2 };
        var snapshots = VmSnapshotAggregator.LiveSnapshots(vm, file => T0.AddMinutes(minutes[file]));

        Assert.Equal(new[] { "Test 2", "Test 1" }, snapshots.Select(s => s.Name));
        Assert.All(snapshots, s => Assert.True(s.IsLive));
        Assert.True(snapshots[0].IsPartial);     // Test 2 only reached disk0.
        Assert.False(snapshots[1].IsPartial);
    }

    [Fact]
    public void A_disk_without_overlays_contributes_nothing()
    {
        Assert.Empty(VmSnapshotAggregator.LiveSnapshots(TwoDisks(), _ => T0));
    }

    [Fact]
    public void Removing_a_middle_level_keeps_the_levels_around_it()
    {
        var disk = new DiskConfig { Id = "disk0", FileName = "disk0.qcow2" };
        disk.PushOverlay("disk0-a.qcow2");
        disk.PushOverlay("disk0-b.qcow2");

        disk.RemoveOverlay("disk0-a.qcow2");

        Assert.Equal(new[] { "disk0-b.qcow2", "disk0.qcow2" }, disk.ChainFromTop);
    }

    [Fact]
    public void The_original_image_cannot_be_removed_as_an_overlay()
    {
        var disk = new DiskConfig { Id = "disk0", FileName = "disk0.qcow2" };
        disk.PushOverlay("disk0-a.qcow2");

        Assert.Throws<InvalidOperationException>(() => disk.RemoveOverlay("disk0.qcow2"));
    }
}

/// <summary>
/// Restoring and deleting live snapshots against the real qemu-img, with a distinct byte pattern written
/// at each level so the tests check data, not just file names. Skipped when QEMU is not installed.
/// </summary>
[Trait("Category", "Integration")]
public class LiveSnapshotOperationTests : IAsyncLifetime
{
    private string _folder = string.Empty;
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

        _folder = Path.Combine(Path.GetTempPath(), "winqemu-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    private bool Available => _images is not null && File.Exists(_qemuIo);

    private Task Io(string file, string command) =>
        ProcessRunner.RunAsync(_qemuIo, new[] { "-c", command, Path.Combine(_folder, file) }, CancellationToken.None);

    private async Task<bool> Holds(string file, string pattern, string offset)
    {
        var result = await ProcessRunner.RunAsync(
            _qemuIo, new[] { "-r", "-c", $"read -P {pattern} {offset} 64k", Path.Combine(_folder, file) }, CancellationToken.None);
        // qemu-io reports a pattern mismatch in its output and still exits 0.
        return result.Succeeded
               && !(result.StandardOutput + result.StandardError).Contains("verification failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>disk0.qcow2 ← "Test 1" ← "Test 2", with 0x11, 0x22, 0x33 written before each step.</summary>
    private async Task<VmConfig> ThreeLevelsAsync()
    {
        var vm = Fixtures.Vm();
        vm.FolderPath = _folder;
        vm.Disks.Clear();
        var disk = new DiskConfig { Id = "disk0", FileName = "disk0.qcow2" };
        vm.Disks.Add(disk);

        await _images!.CreateAsync(Path.Combine(_folder, "disk0.qcow2"), DiskFormat.Qcow2, 16L * 1024 * 1024);
        await Io("disk0.qcow2", "write -P 0x11 0 64k");
        await _images.CreateAsync(Path.Combine(_folder, "disk0-Test 1.qcow2"), DiskFormat.Qcow2, 16L * 1024 * 1024, "disk0.qcow2");
        disk.PushOverlay("disk0-Test 1.qcow2");
        await Io("disk0-Test 1.qcow2", "write -P 0x22 64k 64k");
        await _images.CreateAsync(Path.Combine(_folder, "disk0-Test 2.qcow2"), DiskFormat.Qcow2, 16L * 1024 * 1024, "disk0-Test 1.qcow2");
        disk.PushOverlay("disk0-Test 2.qcow2");
        await Io("disk0-Test 2.qcow2", "write -P 0x33 128k 64k");
        return vm;
    }

    [Fact]
    public async Task Restoring_the_first_snapshot_discards_it_and_everything_after()
    {
        if (!Available) return;
        var vm = await ThreeLevelsAsync();

        await new LiveSnapshotOperations(_images!).RevertAsync(vm, "Test 1");

        Assert.Equal("disk0.qcow2", vm.Disks[0].FileName);
        Assert.False(vm.Disks[0].IsOverlay);
        Assert.False(File.Exists(Path.Combine(_folder, "disk0-Test 1.qcow2")));
        Assert.False(File.Exists(Path.Combine(_folder, "disk0-Test 2.qcow2")));
        Assert.True(await Holds("disk0.qcow2", "0x11", "0"));
        Assert.True(await Holds("disk0.qcow2", "0x00", "64k"));   // Written after Test 1: gone.
    }

    [Fact]
    public async Task Restoring_the_latest_snapshot_only_undoes_the_top_level()
    {
        if (!Available) return;
        var vm = await ThreeLevelsAsync();

        await new LiveSnapshotOperations(_images!).RevertAsync(vm, "Test 2");

        Assert.Equal(new[] { "disk0-Test 1.qcow2", "disk0.qcow2" }, vm.Disks[0].ChainFromTop);
        Assert.True(await Holds("disk0-Test 1.qcow2", "0x22", "64k"));
        Assert.True(await Holds("disk0-Test 1.qcow2", "0x00", "128k"));
    }

    [Fact]
    public async Task Deleting_a_middle_snapshot_keeps_every_byte_and_the_chain_readable()
    {
        if (!Available) return;
        var vm = await ThreeLevelsAsync();

        await new LiveSnapshotOperations(_images!).MergeAsync(vm, "Test 1");

        Assert.Equal(new[] { "disk0-Test 2.qcow2", "disk0.qcow2" }, vm.Disks[0].ChainFromTop);
        Assert.False(File.Exists(Path.Combine(_folder, "disk0-Test 1.qcow2")));
        Assert.True(await Holds("disk0-Test 2.qcow2", "0x11", "0"));
        Assert.True(await Holds("disk0-Test 2.qcow2", "0x22", "64k"));
        Assert.True(await Holds("disk0-Test 2.qcow2", "0x33", "128k"));
    }

    [Fact]
    public async Task Deleting_the_latest_snapshot_folds_it_into_the_level_below()
    {
        if (!Available) return;
        var vm = await ThreeLevelsAsync();

        await new LiveSnapshotOperations(_images!).MergeAsync(vm, "Test 2");

        Assert.Equal(new[] { "disk0-Test 1.qcow2", "disk0.qcow2" }, vm.Disks[0].ChainFromTop);
        Assert.True(await Holds("disk0-Test 1.qcow2", "0x33", "128k"));
    }
}
