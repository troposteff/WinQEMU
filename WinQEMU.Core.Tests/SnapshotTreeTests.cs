using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// The snapshot tree, as VMware's snapshot manager draws it: lineage from the overlay chain for live
/// snapshots, from WinQEMU's own record for internal ones, "Vous êtes ici", and linked clones.
/// </summary>
public class SnapshotTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static VmConfig Machine(string name = "Win", params string[] chainFromTop)
    {
        var config = new VmConfig { Name = name, FolderPath = $@"C:\qemu\{name}" };
        var disk = new DiskConfig { Id = "disk0", FileName = "disk0.qcow2", Format = DiskFormat.Qcow2 };
        config.Disks.Add(disk);
        if (chainFromTop.Length > 1)
        {
            disk.FileName = chainFromTop[0];
            disk.BackingFileName = chainFromTop[1];
            disk.DeeperBackingFiles = chainFromTop.Skip(2).ToList();
        }
        return config;
    }

    private static VmSnapshot Internal(string name, int minutes) => new(name, T0.AddMinutes(minutes), 1, 1, 0);
    private static VmSnapshot Live(string name, int minutes) => new(name, T0.AddMinutes(minutes), 1, 1, 0, IsLive: true);

    private static string Shape(SnapshotTreeNode node) =>
        node.Children.Count == 0 ? node.Title : $"{node.Title}[{string.Join(", ", node.Children.Select(Shape))}]";

    [Fact]
    public void Live_snapshots_follow_their_chain_and_the_machine_stands_on_the_top_one()
    {
        var config = Machine("Win", "disk0-b.qcow2", "disk0-a.qcow2", "disk0.qcow2");

        var tree = SnapshotTree.Build(config, new[] { Live("a", 1), Live("b", 2) }, new[] { config });

        Assert.Equal("État d'origine[a[b[Vous êtes ici]]]", Shape(tree));
    }

    [Fact]
    public void Snapshots_older_than_the_record_hang_from_the_original_state()
    {
        var config = Machine();

        var tree = SnapshotTree.Build(config, new[] { Internal("x", 1), Internal("y", 2) }, new[] { config });

        Assert.Equal("État d'origine[x, y, Vous êtes ici]", Shape(tree));
    }

    [Fact]
    public void A_snapshot_taken_after_a_restore_branches_from_the_restored_one()
    {
        // VMware's tree: take x, then y, go back to x, take z — z is a sibling of y, not its child.
        var config = Machine();
        SnapshotTree.RecordTaken(config, "x", string.Empty);
        SnapshotTree.RecordTaken(config, "y", "x");
        SnapshotTree.RecordRestored(config, "x");
        SnapshotTree.RecordTaken(config, "z", config.CurrentSnapshot);

        var tree = SnapshotTree.Build(config, new[] { Internal("x", 1), Internal("y", 2), Internal("z", 3) }, new[] { config });

        Assert.Equal("État d'origine[x[y, z[Vous êtes ici]]]", Shape(tree));
    }

    [Fact]
    public void A_live_snapshot_taken_after_an_internal_one_hangs_from_it()
    {
        var config = Machine("Win", "disk0-L.qcow2", "disk0.qcow2");
        SnapshotTree.RecordTaken(config, "x", string.Empty);
        SnapshotTree.RecordTaken(config, "L", "x");

        var tree = SnapshotTree.Build(config, new[] { Internal("x", 1), Live("L", 2) }, new[] { config });

        Assert.Equal("État d'origine[x[L[Vous êtes ici]]]", Shape(tree));
    }

    [Fact]
    public void Deleting_a_snapshot_hands_its_children_and_the_machine_to_its_parent()
    {
        var config = Machine();
        SnapshotTree.RecordTaken(config, "x", string.Empty);
        SnapshotTree.RecordTaken(config, "y", "x");
        SnapshotTree.RecordTaken(config, "z", "y");
        SnapshotTree.RecordRestored(config, "y");

        SnapshotTree.RecordDeleted(config, "y", "x");
        var tree = SnapshotTree.Build(config, new[] { Internal("x", 1), Internal("z", 3) }, new[] { config });

        Assert.Equal("État d'origine[x[z, Vous êtes ici]]", Shape(tree));
    }

    [Fact]
    public void A_restored_snapshot_that_is_gone_leaves_the_machine_on_its_closest_ancestor()
    {
        // A live snapshot restored without protection is consumed: its overlay is unstacked.
        var config = Machine();
        SnapshotTree.RecordTaken(config, "x", string.Empty);
        SnapshotTree.RecordTaken(config, "gone", "x");
        SnapshotTree.RecordRestored(config, "gone");

        var tree = SnapshotTree.Build(config, new[] { Internal("x", 1) }, new[] { config });

        Assert.Equal("État d'origine[x[Vous êtes ici]]", Shape(tree));
    }

    [Fact]
    public void A_linked_clone_hangs_from_the_state_it_reads()
    {
        // The clone reads disk0-S1.qcow2 as it was when frozen: the state of "Base de Clone", whose overlay rests on it.
        var original = Machine("Win", "disk0-Base de Clone.qcow2", "disk0-S1.qcow2", "disk0.qcow2");
        var clone = Machine("Clone");
        clone.Disks[0].LinkedBaseFileName = @"..\Win\disk0-S1.qcow2";

        var tree = SnapshotTree.Build(original, new[] { Live("S1", 1), Live("Base de Clone", 2) }, new[] { original, clone });

        Assert.Equal("État d'origine[S1[Base de Clone[Vous êtes ici, Clone]]]", Shape(tree));
        Assert.Same(clone, tree.SelfAndDescendants().Single(n => n.Kind == SnapshotNodeKind.LinkedClone).Clone);
    }

    [Fact]
    public void A_branch_kept_for_a_clone_after_a_restore_comes_back_with_its_clone()
    {
        // S1 was restored while a clone read its overlay: the overlay left the chain but stays on disk.
        var original = Machine("Win", "disk0-S1 (2).qcow2", "disk0.qcow2");
        original.Disks[0].RetainedImages.Add(new RetainedImage { FileName = "disk0-S1.qcow2", BackingFileName = "disk0.qcow2" });
        var clone = Machine("Clone");
        clone.Disks[0].LinkedBaseFileName = @"..\Win\disk0-S1.qcow2";

        var tree = SnapshotTree.Build(original, new[] { Live("S1 (2)", 3) }, new[] { original, clone });

        Assert.Equal("État d'origine[S1 (2)[Vous êtes ici], S1[Clone]]", Shape(tree));
        Assert.True(tree.SelfAndDescendants().Single(n => n.Title == "S1").IsRetained);
    }

    [Fact]
    public void A_recorded_lineage_that_loops_still_draws()
    {
        var config = Machine();
        config.SnapshotLinks.Add(new SnapshotLink { Name = "a", Parent = "b" });
        config.SnapshotLinks.Add(new SnapshotLink { Name = "b", Parent = "a" });

        var tree = SnapshotTree.Build(config, new[] { Internal("a", 1), Internal("b", 2) }, new[] { config });

        Assert.Equal(4, tree.SelfAndDescendants().Count());
    }

    [Fact]
    public void A_line_of_snapshots_is_drawn_straight_and_each_branch_gets_its_row()
    {
        var config = Machine();
        SnapshotTree.RecordTaken(config, "x", string.Empty);
        SnapshotTree.RecordTaken(config, "y", "x");
        SnapshotTree.RecordRestored(config, "x");
        SnapshotTree.RecordTaken(config, "z", "x");

        var tree = SnapshotTree.Build(config, new[] { Internal("x", 1), Internal("y", 2), Internal("z", 3) }, new[] { config });
        var nodes = tree.SelfAndDescendants().ToDictionary(n => n.Title);

        Assert.Equal((0, 0), (nodes["État d'origine"].Column, nodes["État d'origine"].Row));
        Assert.Equal((1, 0), (nodes["x"].Column, nodes["x"].Row));
        Assert.Equal((2, 0), (nodes["y"].Column, nodes["y"].Row));
        Assert.Equal((2, 1), (nodes["z"].Column, nodes["z"].Row));
        Assert.Equal((3, 1), (nodes["Vous êtes ici"].Column, nodes["Vous êtes ici"].Row));
    }

    [Fact]
    public void Where_the_machine_stands_is_what_a_new_snapshot_comes_from()
    {
        var config = Machine("Win", "disk0-b.qcow2", "disk0-a.qcow2", "disk0.qcow2");
        var tree = SnapshotTree.Build(config, new[] { Live("a", 1), Live("b", 2) }, new[] { config });

        Assert.Equal("b", SnapshotTree.CurrentParent(tree));
        Assert.Equal("a", SnapshotTree.ParentOf(tree, "b"));
        Assert.Equal(string.Empty, SnapshotTree.ParentOf(tree, "a"));
    }
}
