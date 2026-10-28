using System.Text.Json;
using System.Text.Json.Serialization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>The library's folders (LibraryTree) and the lines the list shows from them (LibraryLayout).</summary>
public class LibraryTreeTests
{
    private sealed record Machine(Guid Id, string Name, VmState State = VmState.Stopped, DateTime? LastStarted = null);

    private static Machine Vm(string name, VmState state = VmState.Stopped, DateTime? lastStarted = null) =>
        new(Guid.NewGuid(), name, state, lastStarted);

    private static LibraryFolder Add(LibraryTree tree, string name, LibraryFolder? parent = null)
    {
        Assert.Equal(FolderProblem.None, tree.CreateFolder(name, parent?.Id, out var folder));
        return folder!;
    }

    private static IReadOnlyList<string> Lines(LibraryTree tree, IEnumerable<Machine> machines, LibrarySort sort = LibrarySort.Name) =>
        LibraryLayout.Flatten(tree, machines, m => m.Id, m => new MachineSortKey(m.Name, m.State, m.LastStarted), sort)
            .Select(e => new string(' ', e.Depth * 2) + (e.Folder is { } f ? "[" + f.Name + "]" : e.Machine!.Name))
            .ToList();

    [Fact]
    public void Folders_come_first_then_machines_each_level_by_name_and_children_indented()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        var linux = Add(tree, "Linux", work);
        Add(tree, "Archives");
        var debian = Vm("Debian");
        var ubuntu = Vm("ubuntu");
        var windows = Vm("Windows 11");
        tree.MoveMachine(ubuntu.Id, linux.Id);
        tree.MoveMachine(debian.Id, linux.Id);
        tree.MoveMachine(windows.Id, work.Id);

        Assert.Equal(
            new[] { "[Archives]", "[Travail]", "  [Linux]", "    Debian", "    ubuntu", "  Windows 11", "Alpine" },
            Lines(tree, new[] { windows, ubuntu, Vm("Alpine"), debian }));
    }

    [Fact]
    public void A_collapsed_folder_hides_what_it_holds_and_keeps_its_line()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        Add(tree, "Linux", work);
        var vm = Vm("Debian");
        tree.MoveMachine(vm.Id, work.Id);

        work.IsExpanded = false;

        Assert.Equal(new[] { "[Travail]", "Autre" }, Lines(tree, new[] { vm, Vm("Autre") }));
    }

    [Fact]
    public void Sorting_applies_within_every_folder()
    {
        var tree = new LibraryTree();
        var lab = Add(tree, "Labo");
        var now = DateTime.UtcNow;
        var a = Vm("A", VmState.Stopped, now.AddDays(-1));
        var b = Vm("B", VmState.Running, now.AddDays(-3));
        var c = Vm("C", VmState.Paused);
        var d = Vm("D", VmState.Stopped, now);
        tree.MoveMachine(a.Id, lab.Id);
        tree.MoveMachine(b.Id, lab.Id);
        var all = new[] { a, b, c, d };

        Assert.Equal(new[] { "[Labo]", "  B", "  A", "C", "D" }, Lines(tree, all, LibrarySort.State));
        // Never started (C) goes last; within Labo, A was used after B.
        Assert.Equal(new[] { "[Labo]", "  A", "  B", "D", "C" }, Lines(tree, all, LibrarySort.LastUsed));
    }

    [Fact]
    public void Names_are_unique_among_siblings_whatever_the_case_and_never_empty()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        Add(tree, "Linux", work);

        Assert.Equal(FolderProblem.NameTaken, tree.CreateFolder("  travail ", null, out _));
        Assert.Equal(FolderProblem.NameTaken, tree.CreateFolder("LINUX", work.Id, out _));
        Assert.Equal(FolderProblem.EmptyName, tree.CreateFolder("   ", null, out _));
        // The same name elsewhere is fine.
        Assert.Equal(FolderProblem.None, tree.CreateFolder("Linux", null, out var trimmed));
        Assert.Equal("Linux", trimmed!.Name);
        // Renaming a folder to its own name with another case is not a clash with itself.
        Assert.Equal(FolderProblem.None, tree.Rename(work.Id, "TRAVAIL"));
        Assert.Equal("TRAVAIL", work.Name);
        Assert.Equal(FolderProblem.NameTaken, tree.Rename(work.Id, "linux"));
        Assert.Equal("Nouveau dossier (2)", new LibraryTree { Folders = { new LibraryFolder { Name = "Nouveau dossier" } } }.UniqueName("Nouveau dossier", null));
    }

    [Fact]
    public void A_folder_cannot_move_into_itself_or_a_descendant()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        var linux = Add(tree, "Linux", work);
        var old = Add(tree, "Anciennes", linux);

        Assert.Equal(FolderProblem.IntoItself, tree.MoveFolder(work.Id, work.Id));
        Assert.Equal(FolderProblem.IntoItself, tree.MoveFolder(work.Id, old.Id));
        Assert.Null(work.ParentId);

        Assert.Equal(FolderProblem.None, tree.MoveFolder(old.Id, null));
        Assert.Null(old.ParentId);
        Assert.Equal(FolderProblem.None, tree.MoveFolder(work.Id, old.Id));
        Assert.Equal(new[] { "Anciennes", "Travail", "Linux" }, tree.PathOf(linux.Id).Select(f => f.Name));
    }

    [Fact]
    public void A_folder_does_not_move_next_to_one_of_the_same_name()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        Add(tree, "Linux", work);
        var linux = Add(tree, "Linux");

        Assert.Equal(FolderProblem.NameTaken, tree.MoveFolder(linux.Id, work.Id));
        Assert.Null(linux.ParentId);
    }

    [Fact]
    public void Deleting_a_folder_moves_its_content_to_its_parent_and_never_a_machine()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        Add(tree, "Linux", work);
        var projects = Add(tree, "Projets", work);
        var linux = Add(tree, "Linux", projects);
        var inProjects = Vm("Debian");
        var inLinux = Vm("Ubuntu");
        tree.MoveMachine(inProjects.Id, projects.Id);
        tree.MoveMachine(inLinux.Id, linux.Id);

        Assert.True(tree.DeleteFolder(projects.Id));

        Assert.Null(tree.Find(projects.Id));
        Assert.Equal(work.Id, tree.FolderOf(inProjects.Id));
        // Its "Linux" lands next to another "Linux": numbered, and still holding its machine.
        Assert.Equal(work.Id, linux.ParentId);
        Assert.Equal("Linux (2)", linux.Name);
        Assert.Equal(linux.Id, tree.FolderOf(inLinux.Id));

        // At the root, its machines go to the root.
        Assert.True(tree.DeleteFolder(work.Id));
        Assert.Null(tree.FolderOf(inProjects.Id));
        Assert.False(tree.Machines.ContainsKey(inProjects.Id));
        Assert.Null(linux.ParentId);
    }

    [Fact]
    public void A_machine_whose_folder_is_gone_is_at_the_root()
    {
        var tree = new LibraryTree();
        var vm = Vm("Debian");
        tree.Machines[vm.Id] = "disparu";

        Assert.Null(tree.FolderOf(vm.Id));
        Assert.Equal(new[] { "Debian" }, Lines(tree, new[] { vm }));
        Assert.Equal(FolderProblem.NotFound, tree.MoveMachine(vm.Id, "disparu"));
    }

    [Fact]
    public void Repair_undoes_what_a_hand_edit_can_break_and_forgets_machines_no_longer_in_the_library()
    {
        var kept = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var lost = Guid.NewGuid();
        var tree = new LibraryTree
        {
            Folders =
            {
                new LibraryFolder { Id = "a", Name = "A", ParentId = "b" },
                new LibraryFolder { Id = "b", Name = "B", ParentId = "a" },
                new LibraryFolder { Id = "c", Name = "C", ParentId = "nowhere" },
                new LibraryFolder { Id = "c", Name = "Doublon" },
                new LibraryFolder { Id = "d", Name = " c " },
                new LibraryFolder { Id = "e", Name = "" }
            },
            Machines = { [kept] = "a", [gone] = "a", [lost] = "nowhere" }
        };

        Assert.True(tree.Repair(new[] { kept, lost }, "Sans nom"));

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, tree.Folders.Select(f => f.Id));
        // The loop is cut: both folders now reach the root.
        Assert.All(tree.Folders, f => Assert.True(tree.PathOf(f.Id).First().ParentId is null));
        Assert.Null(tree.Find("c")!.ParentId);
        Assert.Equal("c (2)", tree.Find("d")!.Name);
        Assert.Equal("Sans nom", tree.Find("e")!.Name);
        Assert.Equal(new[] { kept }, tree.Machines.Keys);

        Assert.False(tree.Repair(new[] { kept }, "Sans nom"));
    }

    [Fact]
    public void Folders_count_every_machine_below_them_and_those_running()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        var linux = Add(tree, "Linux", work);
        var empty = Add(tree, "Vide");
        var running = Vm("Debian", VmState.Running);
        var paused = Vm("Ubuntu", VmState.Paused);
        var off = Vm("Windows");
        tree.MoveMachine(running.Id, linux.Id);
        tree.MoveMachine(paused.Id, work.Id);
        tree.MoveMachine(off.Id, linux.Id);

        var counts = LibraryLayout.Count(tree, new[] { running, paused, off, Vm("Racine", VmState.Running) }, m => m.Id, m => m.State);

        Assert.Equal((3, 2), counts[work.Id]);
        Assert.Equal((2, 1), counts[linux.Id]);
        Assert.Equal((0, 0), counts[empty.Id]);
    }

    [Fact]
    public void Creating_a_folder_inside_a_collapsed_one_opens_it()
    {
        var tree = new LibraryTree();
        var work = Add(tree, "Travail");
        var linux = Add(tree, "Linux", work);
        work.IsExpanded = false;
        linux.IsExpanded = false;

        Add(tree, "Anciennes", linux);

        Assert.True(work.IsExpanded);
        Assert.True(linux.IsExpanded);
    }

    [Fact]
    public void The_list_is_brought_in_line_without_ever_removing_the_selected_line()
    {
        var random = new Random(1234);
        var pool = Enumerable.Range(0, 12).Select(i => "line " + i).ToArray();
        for (var round = 0; round < 500; round++)
        {
            var rows = new System.Collections.ObjectModel.ObservableCollection<string>(
                pool.Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next()));
            var target = pool.Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next()).ToList();
            var keep = rows.Count > 0 ? rows[random.Next(rows.Count)] : null;
            var keptRemoved = false;
            rows.CollectionChanged += (_, e) =>
            {
                if (e.OldItems?.Cast<string>().Any(r => ReferenceEquals(r, keep)) == true)
                    keptRemoved = true;
            };

            LibraryLayout.Synchronize(rows, target, keep);

            Assert.Equal(target, rows);
            // Removed only when it is no longer wanted at all.
            Assert.Equal(keep is not null && !target.Contains(keep), keptRemoved);
        }
    }

    [Fact]
    public void Settings_without_folders_still_load_and_folders_survive_a_round_trip()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        var old = JsonSerializer.Deserialize<AppSettings>("""{ "QemuDirectory": "C:\\qemu", "Language": "fr" }""", options)!;
        Assert.Empty(old.Library.Folders);
        Assert.Empty(old.Library.Machines);
        Assert.Equal(LibrarySort.Name, old.Library.Sort);

        var nulls = JsonSerializer.Deserialize<AppSettings>("""{ "Library": { "Folders": null, "Machines": null } }""", options)!;
        Assert.Empty(nulls.Library.Folders);
        Assert.Empty(nulls.Library.Machines);

        var settings = new AppSettings();
        Assert.Equal(FolderProblem.None, settings.Library.CreateFolder("Travail", null, out var work));
        work!.IsExpanded = false;
        var vm = Guid.NewGuid();
        settings.Library.MoveMachine(vm, work.Id);
        settings.Library.Sort = LibrarySort.LastUsed;

        var json = JsonSerializer.Serialize(settings, options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, options)!;

        Assert.Equal(work.Id, back.Library.FolderOf(vm));
        Assert.False(back.Library.Find(work.Id)!.IsExpanded);
        Assert.Equal("Travail", back.Library.Find(work.Id)!.Name);
        Assert.Equal(LibrarySort.LastUsed, back.Library.Sort);
        Assert.Contains("\"LastUsed\"", json);
    }
}
