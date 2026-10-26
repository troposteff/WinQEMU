using System.Text.Json;
using System.Text.Json.Serialization;
using WinQEMU.Core.Models;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Live snapshots stack qcow2 overlays. Only one level used to be recorded, so reverting the top one
/// left the overlay underneath with no base: still an overlay on disk, no longer one the shell would undo.
/// </summary>
public class OverlayChainTests
{
    private static DiskConfig Disk() => new() { Id = "disk0", FileName = "disk0.qcow2" };

    [Fact]
    public void A_first_live_snapshot_layers_the_disk_on_its_image()
    {
        var disk = Disk();
        disk.PushOverlay("disk0-a.qcow2");

        Assert.Equal("disk0-a.qcow2", disk.FileName);
        Assert.Equal("disk0.qcow2", disk.BackingFileName);
        Assert.Empty(disk.DeeperBackingFiles);
        Assert.Equal(1, disk.OverlayDepth);
    }

    [Fact]
    public void Stacked_snapshots_keep_every_level()
    {
        var disk = Disk();
        disk.PushOverlay("disk0-a.qcow2");
        disk.PushOverlay("disk0-b.qcow2");
        disk.PushOverlay("disk0-c.qcow2");

        Assert.Equal("disk0-c.qcow2", disk.FileName);
        Assert.Equal("disk0-b.qcow2", disk.BackingFileName);
        Assert.Equal(new[] { "disk0-a.qcow2", "disk0.qcow2" }, disk.DeeperBackingFiles);
        Assert.Equal(3, disk.OverlayDepth);
    }

    [Fact]
    public void Each_level_can_be_undone_down_to_the_original_image()
    {
        var disk = Disk();
        disk.PushOverlay("disk0-a.qcow2");
        disk.PushOverlay("disk0-b.qcow2");

        Assert.Equal("disk0-b.qcow2", disk.PopOverlay());
        // The bug: here the disk used to lose its base and stop being an overlay.
        Assert.True(disk.IsOverlay);
        Assert.Equal("disk0-a.qcow2", disk.FileName);
        Assert.Equal("disk0.qcow2", disk.BackingFileName);

        Assert.Equal("disk0-a.qcow2", disk.PopOverlay());
        Assert.False(disk.IsOverlay);
        Assert.Equal("disk0.qcow2", disk.FileName);
        Assert.Equal(0, disk.OverlayDepth);
    }

    [Fact]
    public void Popping_a_disk_that_is_no_overlay_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Disk().PopOverlay());
    }

    [Fact]
    public void The_chain_survives_a_save_and_reload()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var disk = Disk();
        disk.PushOverlay("disk0-a.qcow2");
        disk.PushOverlay("disk0-b.qcow2");

        var reloaded = JsonSerializer.Deserialize<DiskConfig>(JsonSerializer.Serialize(disk, options), options)!;

        Assert.Equal("disk0-b.qcow2", reloaded.FileName);
        Assert.Equal("disk0-a.qcow2", reloaded.BackingFileName);
        Assert.Equal(new[] { "disk0.qcow2" }, reloaded.DeeperBackingFiles);
    }

    [Fact]
    public void A_descriptor_from_before_chains_still_loads_as_a_single_overlay()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        const string json = """{ "Id": "disk0", "FileName": "disk0-a.qcow2", "BackingFileName": "disk0.qcow2" }""";

        var disk = JsonSerializer.Deserialize<DiskConfig>(json, options)!;

        Assert.True(disk.IsOverlay);
        Assert.Empty(disk.DeeperBackingFiles);
        Assert.Equal("disk0.qcow2", disk.PopOverlay() == "disk0-a.qcow2" ? disk.FileName : null);
    }
}
