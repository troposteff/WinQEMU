using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>Automatic snapshots, on a real qcow2 image with the real qemu-img (skipped without QEMU).</summary>
[Trait("Category", "Integration")]
public sealed class AutoSnapshotTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-auto-" + Guid.NewGuid().ToString("N"));
    private QemuImgService? _images;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        if ((await QemuLocator.DetectAsync()).Installation is { } installation)
            _images = new QemuImgService(installation);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_folder, true);
        return Task.CompletedTask;
    }

    [Fact]
    public void Names_sort_by_date_and_are_ASCII()
    {
        var name = AutoSnapshots.NameFor(new DateTime(2026, 10, 28, 9, 5, 0));
        Assert.Equal("Auto 2026-10-28 09h05", name);
        Assert.True(AutoSnapshots.IsAutomatic(name));
        Assert.Null(QemuImgService.ValidateSnapshotName(name));
        Assert.False(AutoSnapshots.IsAutomatic("Auto avant mise à jour"));
    }

    [Fact]
    public void The_oldest_beyond_the_number_kept_are_the_surplus_and_the_user_s_own_never()
    {
        var surplus = AutoSnapshots.Surplus(new[]
        {
            "Auto 2026-10-01 08h00", "Avant mise a jour", "Auto 2026-10-03 08h00", "Auto 2026-10-02 08h00", "Auto 2026-10-04 08h00"
        }, keep: 2);

        Assert.Equal(new[] { "Auto 2026-10-01 08h00", "Auto 2026-10-02 08h00" }, surplus);
    }

    [Fact]
    public async Task A_snapshot_is_taken_once_an_interval_and_the_oldest_go()
    {
        if (_images is null)
            return;
        await _images.CreateAsync(Path.Combine(_folder, "disk0.qcow2"), DiskFormat.Qcow2, 64L << 20, backingFile: null);
        await _images.CreateOfflineSnapshotAsync(Path.Combine(_folder, "disk0.qcow2"), "Avant mise a jour");
        var config = new VmConfig
        {
            Name = "Test",
            FolderPath = _folder,
            AutoSnapshot = true,
            AutoSnapshotHours = 24,
            AutoSnapshotKeep = 2
        };
        config.Disks.Add(new DiskConfig { Id = "disk0", FileName = "disk0.qcow2", Format = DiskFormat.Qcow2 });
        var library = new[] { config };
        var day = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

        Assert.NotNull(await AutoSnapshots.TakeIfDueAsync(config, _images, library, day));
        // Within the interval: nothing.
        Assert.Null(await AutoSnapshots.TakeIfDueAsync(config, _images, library, day.AddHours(5)));
        Assert.NotNull(await AutoSnapshots.TakeIfDueAsync(config, _images, library, day.AddDays(1)));
        var third = await AutoSnapshots.TakeIfDueAsync(config, _images, library, day.AddDays(2));

        var names = (await _images.GetInfoAsync(Path.Combine(_folder, "disk0.qcow2"))).Snapshots.Select(s => s.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("Avant mise a jour", names);
        Assert.Contains(third!, names);
        Assert.Equal(2, names.Count(AutoSnapshots.IsAutomatic));
        // The machine stands on the last one, which comes from the one before.
        Assert.Equal(third, config.CurrentSnapshot);
        Assert.Equal(day.AddDays(2), config.LastAutoSnapshotUtc);
        Assert.DoesNotContain(config.SnapshotLinks, l => l.Name == AutoSnapshots.NameFor(day.ToLocalTime()));
    }

    [Fact]
    public void Nothing_is_due_without_a_qcow2_disk_or_when_off()
    {
        var config = new VmConfig { AutoSnapshot = true };
        config.Disks.Add(new DiskConfig { Id = "disk0", FileName = "disk0.img", Format = DiskFormat.Raw });
        Assert.False(AutoSnapshots.IsDue(config, DateTime.UtcNow));
        config.Disks[0].Format = DiskFormat.Qcow2;
        config.AutoSnapshot = false;
        Assert.False(AutoSnapshots.IsDue(config, DateTime.UtcNow));
    }
}
