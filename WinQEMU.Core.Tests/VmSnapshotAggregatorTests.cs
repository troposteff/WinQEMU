using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Assembling a machine-level snapshot from the per-image ones. The stake is a partial snapshot: if it
/// passes for complete, restoring it puts some disks back and leaves the others in the present.
/// </summary>
public class VmSnapshotAggregatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static DiskSnapshot Snap(string name, int minutesAfterNoon = 0, long vmStateSize = 0) =>
        new($"id-{name}", name, vmStateSize, Noon.AddMinutes(minutesAfterNoon));

    private static IReadOnlyCollection<DiskSnapshot> Disk(params DiskSnapshot[] snapshots) => snapshots;

    // ---- Which disks take part ----

    private static VmConfig VmWith(params DiskFormat[] formats)
    {
        var vm = new VmConfig { Name = "Test", FolderPath = @"C:\vm" };
        vm.Disks.Clear();
        for (var i = 0; i < formats.Length; i++)
            vm.Disks.Add(new DiskConfig { Id = $"disk{i}", FileName = $"disk{i}", Format = formats[i] });
        return vm;
    }

    [Fact]
    public void Only_qcow2_disks_can_hold_a_snapshot()
    {
        var vm = VmWith(DiskFormat.Qcow2, DiskFormat.Raw, DiskFormat.Qcow2, DiskFormat.Vhdx);

        Assert.Equal(new[] { "disk0", "disk2" }, VmSnapshotAggregator.SnapshotableDisks(vm).Select(d => d.Id));
        Assert.Equal(new[] { "disk1", "disk3" }, VmSnapshotAggregator.UnsupportedDisks(vm).Select(d => d.Id));
    }

    [Fact]
    public void A_machine_of_only_qcow2_disks_excludes_nothing()
    {
        var vm = VmWith(DiskFormat.Qcow2, DiskFormat.Qcow2);

        Assert.Equal(2, VmSnapshotAggregator.SnapshotableDisks(vm).Count);
        Assert.Empty(VmSnapshotAggregator.UnsupportedDisks(vm));
    }

    [Fact]
    public void A_machine_without_a_single_qcow2_disk_can_hold_nothing()
    {
        var vm = VmWith(DiskFormat.Raw, DiskFormat.Vmdk);

        Assert.Empty(VmSnapshotAggregator.SnapshotableDisks(vm));
        Assert.Equal(2, VmSnapshotAggregator.UnsupportedDisks(vm).Count);
    }

    [Fact]
    public void A_machine_with_no_disk_at_all_holds_nothing()
    {
        var vm = VmWith();

        Assert.Empty(VmSnapshotAggregator.SnapshotableDisks(vm));
        Assert.Empty(VmSnapshotAggregator.UnsupportedDisks(vm));
    }

    // ---- Aggregation ----

    [Fact]
    public void No_disks_gives_no_snapshots()
    {
        Assert.Empty(VmSnapshotAggregator.Aggregate(Array.Empty<IReadOnlyCollection<DiskSnapshot>>()));
    }

    [Fact]
    public void An_empty_disk_gives_no_snapshots()
    {
        Assert.Empty(VmSnapshotAggregator.Aggregate(new[] { Disk() }));
    }

    [Fact]
    public void A_single_disk_snapshot_spans_that_one_disk_and_is_complete()
    {
        var result = VmSnapshotAggregator.Aggregate(new[] { Disk(Snap("avant-maj")) });

        var snapshot = Assert.Single(result);
        Assert.Equal("avant-maj", snapshot.Name);
        Assert.Equal(1, snapshot.PresentOnDisks);
        Assert.Equal(1, snapshot.TotalDisks);
        Assert.False(snapshot.IsPartial);
    }

    [Fact]
    public void A_name_present_on_every_disk_is_one_complete_snapshot()
    {
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("avant-maj")),
            Disk(Snap("avant-maj", minutesAfterNoon: 1))
        });

        var snapshot = Assert.Single(result);
        Assert.Equal(2, snapshot.PresentOnDisks);
        Assert.Equal(2, snapshot.TotalDisks);
        Assert.False(snapshot.IsPartial);
    }

    [Fact]
    public void A_name_missing_from_one_disk_is_flagged_partial()
    {
        // The case that must never pass unnoticed: restoring it would mix two eras of the same machine.
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("complet"), Snap("partiel")),
            Disk(Snap("complet"))
        });

        var partial = Assert.Single(result, s => s.Name == "partiel");
        Assert.Equal(1, partial.PresentOnDisks);
        Assert.Equal(2, partial.TotalDisks);
        Assert.True(partial.IsPartial);

        var complete = Assert.Single(result, s => s.Name == "complet");
        Assert.False(complete.IsPartial);
    }

    [Fact]
    public void The_creation_time_is_the_oldest_of_the_group()
    {
        // Writing the second disk happens after the first; the snapshot was taken at the first.
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("avant-maj", minutesAfterNoon: 5)),
            Disk(Snap("avant-maj", minutesAfterNoon: 0))
        });

        Assert.Equal(Noon, Assert.Single(result).CreatedAt);
    }

    [Fact]
    public void Snapshots_are_ordered_newest_first()
    {
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("vieux", 0), Snap("recent", 120), Snap("moyen", 60))
        });

        Assert.Equal(new[] { "recent", "moyen", "vieux" }, result.Select(s => s.Name));
    }

    [Fact]
    public void Ordering_uses_the_most_recent_disk_of_each_group()
    {
        // Group A was started first but finished last; a user reading a history expects the last
        // completed one at the top.
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("a", 0), Snap("b", 10)),
            Disk(Snap("a", 30), Snap("b", 11))
        });

        Assert.Equal(new[] { "a", "b" }, result.Select(s => s.Name));
    }

    [Fact]
    public void Memory_state_is_reported_when_any_disk_carries_it()
    {
        // savevm writes the guest memory into one image only; the machine still has that memory state.
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("avec-memoire", vmStateSize: 0)),
            Disk(Snap("avec-memoire", vmStateSize: 512 * 1024 * 1024))
        });

        var snapshot = Assert.Single(result);
        Assert.Equal(512L * 1024 * 1024, snapshot.VmStateSizeBytes);
        Assert.True(snapshot.HasMemoryState);
    }

    [Fact]
    public void A_disk_only_snapshot_reports_no_memory_state()
    {
        var snapshot = Assert.Single(VmSnapshotAggregator.Aggregate(new[] { Disk(Snap("disque-seul")) }));

        Assert.Equal(0, snapshot.VmStateSizeBytes);
        Assert.False(snapshot.HasMemoryState);
    }

    [Fact]
    public void A_repeated_name_on_one_disk_does_not_inflate_the_coverage()
    {
        // Guards the partial detection: counting raw entries would make one disk look like two and a
        // genuinely partial snapshot would pass for complete.
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("doublon"), Snap("doublon", minutesAfterNoon: 1)),
            Disk()
        });

        var snapshot = Assert.Single(result);
        Assert.Equal(1, snapshot.PresentOnDisks);
        Assert.Equal(2, snapshot.TotalDisks);
        Assert.True(snapshot.IsPartial);
    }

    [Fact]
    public void Names_are_matched_case_sensitively_as_qemu_stores_them()
    {
        // qemu-img treats them as distinct tags, so folding them would merge two real snapshots.
        var result = VmSnapshotAggregator.Aggregate(new[] { Disk(Snap("Avant"), Snap("avant")) });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void A_three_disk_machine_reports_partial_coverage_precisely()
    {
        var result = VmSnapshotAggregator.Aggregate(new[]
        {
            Disk(Snap("deux-tiers")),
            Disk(Snap("deux-tiers")),
            Disk()
        });

        var snapshot = Assert.Single(result);
        Assert.Equal(2, snapshot.PresentOnDisks);
        Assert.Equal(3, snapshot.TotalDisks);
        Assert.True(snapshot.IsPartial);
    }
}
