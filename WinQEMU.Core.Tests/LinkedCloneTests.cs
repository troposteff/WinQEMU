using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>
/// The protection rule on descriptors alone: which operations on the original would touch an image a
/// linked clone reads. No file is involved, so every chain shape can be spelled out.
/// </summary>
public class LinkedCloneGuardTests
{
    private const string Library = @"C:\lib";

    /// <summary>An original whose disk writes to the top of <paramref name="chainTopFirst"/>.</summary>
    private static VmConfig Original(params string[] chainTopFirst)
    {
        var vm = Fixtures.Vm("Debian");
        vm.FolderPath = Path.Combine(Library, "Debian");
        var disk = vm.Disks[0];
        disk.FileName = chainTopFirst[^1];
        foreach (var overlay in chainTopFirst.Reverse().Skip(1))
            disk.PushOverlay(overlay);
        return vm;
    }

    private static VmConfig CloneOn(string baseFile, string name = "Copie")
    {
        var vm = Fixtures.Vm(name);
        vm.FolderPath = Path.Combine(Library, name);
        vm.Disks[0].LinkedBaseFileName = @"..\Debian\" + baseFile;
        return vm;
    }

    private static LinkedCloneProtection Deps(params VmConfig[] machines) =>
        LinkedCloneProtection.Of(machines);

    [Fact]
    public void The_layer_frozen_for_a_clone_can_neither_be_deleted_nor_merged()
    {
        var original = Original("disk0-Base de Copie.qcow2", "disk0.qcow2");
        var deps = Deps(original, CloneOn("disk0.qcow2"));

        var reason = LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Base de Copie", deps);

        Assert.NotNull(reason);
        Assert.Contains("« Copie »", reason);
        Assert.NotNull(LinkedCloneGuard.WhyOverlayCannotBeMerged(original, original.Disks[0], 0, deps));
    }

    [Fact]
    public void A_snapshot_taken_after_the_clone_stays_free()
    {
        var original = Original("disk0-Plus tard.qcow2", "disk0-Base de Copie.qcow2", "disk0.qcow2");
        var deps = Deps(original, CloneOn("disk0.qcow2"));

        Assert.Null(LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Plus tard", deps));
        Assert.Null(LinkedCloneGuard.WhyOverlayCannotBeMerged(original, original.Disks[0], 0, deps));
        Assert.NotNull(LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Base de Copie", deps));
    }

    [Fact]
    public void Snapshots_under_the_base_are_locked_too()
    {
        // The clone was made while the original wrote to the overlay of "Avant": that overlay is the base,
        // and deleting "Avant" would merge it away.
        var original = Original("disk0-Base de Copie.qcow2", "disk0-Avant.qcow2", "disk0.qcow2");
        var deps = Deps(original, CloneOn("disk0-Avant.qcow2"));

        Assert.NotNull(LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Avant", deps));
        Assert.NotNull(LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Base de Copie", deps));
    }

    [Fact]
    public void Without_a_clone_nothing_is_locked()
    {
        var original = Original("disk0-Base de Copie.qcow2", "disk0.qcow2");
        var deps = Deps(original);

        Assert.Null(LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Base de Copie", deps));
        Assert.Null(LinkedCloneGuard.WhyFilesCannotBeDeleted(original, deps));
    }

    [Fact]
    public void The_original_files_cannot_be_deleted_but_the_clone_files_can()
    {
        var original = Original("disk0-Base de Copie.qcow2", "disk0.qcow2");
        var clone = CloneOn("disk0.qcow2");
        var deps = Deps(original, clone);

        Assert.NotNull(LinkedCloneGuard.WhyFilesCannotBeDeleted(original, deps));
        Assert.Null(LinkedCloneGuard.WhyFilesCannotBeDeleted(clone, deps));
    }

    [Fact]
    public void A_folder_whose_name_extends_the_original_is_not_mistaken_for_it()
    {
        var original = Original("disk0.qcow2");
        original.FolderPath = Path.Combine(Library, "Debian 2");
        var deps = Deps(original, CloneOn("disk0.qcow2"));

        Assert.Null(LinkedCloneGuard.WhyFilesCannotBeDeleted(original, deps));
    }

    [Fact]
    public void A_base_cannot_be_attached_to_another_machine()
    {
        var deps = Deps(CloneOn("disk0.qcow2"));

        Assert.NotNull(LinkedCloneGuard.WhyImageCannotBeAttached(@"C:\LIB\Debian\disk0.qcow2", deps));
        Assert.Null(LinkedCloneGuard.WhyImageCannotBeAttached(@"C:\lib\Debian\disk1.qcow2", deps));
    }

    [Fact]
    public void Several_clones_are_all_named()
    {
        var original = Original("disk0-Base de B.qcow2", "disk0-Base de A.qcow2", "disk0.qcow2");
        var deps = Deps(original, CloneOn("disk0.qcow2", "A"), CloneOn("disk0-Base de A.qcow2", "B"));

        var reason = LinkedCloneGuard.WhyFilesCannotBeDeleted(original, deps)!;

        Assert.Contains("les clones liés", reason);
        Assert.Contains("« A »", reason);
        Assert.Contains("« B »", reason);
    }

    [Fact]
    public void Images_kept_for_a_clone_protect_what_they_are_layered_on()
    {
        // After restoring "Avant", the original writes to a fresh overlay on disk0; the old overlays only
        // remain because the clone reads "Base de Copie" -> "Avant" -> disk0. Merging the fresh overlay into
        // disk0 would change what the clone reads, although its base is no longer in the chain.
        var original = Original("disk0-Avant (2).qcow2", "disk0.qcow2");
        original.Disks[0].RetainedImages.Add(new RetainedImage { FileName = "disk0-Avant.qcow2", BackingFileName = "disk0.qcow2" });
        var deps = Deps(original, CloneOn("disk0-Avant.qcow2"));

        Assert.NotNull(LinkedCloneGuard.WhyLiveSnapshotCannotBeDeleted(original, "Avant (2)", deps));
        Assert.True(deps.IsProtected(@"C:\lib\Debian\disk0.qcow2"));
        Assert.False(deps.IsProtected(@"C:\lib\Debian\disk0-Avant (2).qcow2"));
    }

    [Fact]
    public void Non_qcow2_disks_rule_out_a_linked_clone()
    {
        var vm = Fixtures.Vm();
        vm.Disks.Add(new DiskConfig { Id = "disk1", FileName = "data.img", Format = DiskFormat.Raw });

        var reason = VmLibrary.WhyNoLinkedClone(vm);

        Assert.NotNull(reason);
        Assert.Contains("disk1 (raw)", reason);
        Assert.Null(VmLibrary.WhyNoLinkedClone(Fixtures.Vm()));
    }
}

/// <summary>
/// Linked clones against the real qemu-img, checking the bytes each machine reads. Skipped when QEMU is not
/// installed.
/// </summary>
[Trait("Category", "Integration")]
public class LinkedCloneTests : IAsyncLifetime
{
    private const long SmallDisk = 16L * 1024 * 1024;

    private string _root = string.Empty;
    private QemuImgService? _images;
    private string _qemuIo = string.Empty;
    private VmManager _manager = null!;

    public async Task InitializeAsync()
    {
        var installation = (await QemuLocator.DetectAsync()).Installation;
        if (installation is not null)
        {
            _images = new QemuImgService(installation);
            _qemuIo = Path.Combine(Path.GetDirectoryName(installation.QemuImgPath)!, "qemu-io.exe");
        }

        _root = Path.Combine(Path.GetTempPath(), "winqemu-linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _manager = new VmManager(new AppSettings { LibraryRoot = _root });
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private bool Available => _images is not null && File.Exists(_qemuIo);

    private Task Io(string path, string command) =>
        ProcessRunner.RunAsync(_qemuIo, new[] { "-c", command, path }, CancellationToken.None);

    private async Task<bool> Holds(string path, string pattern, string offset)
    {
        var result = await ProcessRunner.RunAsync(
            _qemuIo, new[] { "-r", "-c", $"read -P {pattern} {offset} 64k", path }, CancellationToken.None);
        // qemu-io reports a pattern mismatch in its output and still exits 0.
        return result.Succeeded
               && !(result.StandardOutput + result.StandardError).Contains("verification failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string Top(VmConfig vm, int disk = 0) => vm.ResolvePath(vm.Disks[disk].FileName);

    /// <summary>"Debian" in the library, one qcow2 disk holding 0x11 at 0, loaded through the manager.</summary>
    private async Task<VmConfig> OriginalAsync()
    {
        var folder = Path.Combine(_root, "Debian");
        var vm = Fixtures.Vm("Debian");
        vm.FolderPath = folder;
        vm.NetworkAdapters.Add(new NetworkAdapterConfig { Id = "net0", MacAddress = "52:54:00:aa:bb:cc" });
        await _images!.CreateAsync(Path.Combine(folder, "disk0.qcow2"), DiskFormat.Qcow2, SmallDisk);
        await Io(Path.Combine(folder, "disk0.qcow2"), "write -P 0x11 0 64k");
        new VmLibrary(_root, _images).Save(vm);

        await _manager.InitializeAsync();
        return Assert.Single(_manager.VirtualMachines);
    }

    [Fact]
    public async Task The_clone_reads_the_original_and_each_then_writes_only_to_itself()
    {
        if (!Available) return;
        var original = await OriginalAsync();

        var clone = await _manager.CreateLinkedCloneAsync(original, "Copie");

        Assert.True(await Holds(Top(clone), "0x11", "0"));

        await Io(Top(clone), "write -P 0x22 64k 64k");
        await Io(Top(original), "write -P 0x33 128k 64k");

        Assert.True(await Holds(Top(clone), "0x22", "64k"));
        Assert.True(await Holds(Top(clone), "0x00", "128k"));     // The original's later write: not seen.
        Assert.True(await Holds(Top(original), "0x33", "128k"));
        Assert.True(await Holds(Top(original), "0x00", "64k"));   // The clone's write: not seen.
        Assert.True(await Holds(Path.Combine(original.FolderPath, "disk0.qcow2"), "0x00", "128k")); // The base: untouched.
    }

    [Fact]
    public async Task Both_descriptors_record_the_link_and_reload_as_written()
    {
        if (!Available) return;
        var original = await OriginalAsync();

        var clone = await _manager.CreateLinkedCloneAsync(original, "Copie");

        Assert.Equal(new[] { "disk0-Base de Copie.qcow2", "disk0.qcow2" }, original.Disks[0].ChainFromTop);
        Assert.False(clone.Disks[0].IsOverlay);
        Assert.Equal(@"..\Debian\disk0.qcow2", clone.Disks[0].LinkedBaseFileName);
        Assert.NotEqual(original.Id, clone.Id);
        Assert.NotEqual("52:54:00:aa:bb:cc", clone.NetworkAdapters[0].MacAddress);

        // Only the clone's own writes take space: the overlay starts nearly empty.
        Assert.True(new FileInfo(Top(clone)).Length < 1024 * 1024);

        _manager.ReloadLibrary();
        var reloaded = _manager.VirtualMachines.Single(m => m.Id == original.Id);
        Assert.Equal(original.Disks[0].ChainFromTop, reloaded.Disks[0].ChainFromTop);
        Assert.Equal(@"..\Debian\disk0.qcow2", _manager.VirtualMachines.Single(m => m.Id == clone.Id).Disks[0].LinkedBaseFileName);

        // And the frozen layer appears as a live snapshot of the original, locked.
        Assert.Contains(VmSnapshotAggregator.LiveSnapshots(reloaded, _ => DateTimeOffset.UtcNow), s => s.Name == "Base de Copie");
        Assert.NotNull(_manager.WhyLiveSnapshotCannotBeDeleted(reloaded, "Base de Copie"));
    }

    [Fact]
    public async Task The_original_cannot_be_deleted_with_its_files_until_the_clone_is_gone()
    {
        if (!Available) return;
        var original = await OriginalAsync();
        var clone = await _manager.CreateLinkedCloneAsync(original, "Copie");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.DeleteVirtualMachineAsync(original, deleteFiles: true));
        Assert.True(File.Exists(Path.Combine(original.FolderPath, "disk0.qcow2")));
        Assert.Contains(original, _manager.VirtualMachines);

        await _manager.DeleteVirtualMachineAsync(clone, deleteFiles: true);
        Assert.Null(_manager.WhyLiveSnapshotCannotBeDeleted(original, "Base de Copie"));
        await _manager.DeleteVirtualMachineAsync(original, deleteFiles: true);
        Assert.False(Directory.Exists(original.FolderPath));
    }

    [Fact]
    public async Task Moving_the_whole_library_keeps_the_clone_readable()
    {
        if (!Available) return;
        var original = await OriginalAsync();
        var clone = await _manager.CreateLinkedCloneAsync(original, "Copie");
        var cloneDisk = Path.GetRelativePath(_root, Top(clone));
        await _manager.DisposeAsync();

        var moved = _root + "-moved";
        Directory.Move(_root, moved);
        _root = moved;   // So DisposeAsync cleans up the new place.

        Assert.True(await Holds(Path.Combine(moved, cloneDisk), "0x11", "0"));
    }

    [Fact]
    public async Task A_second_clone_gets_its_own_layer_and_name()
    {
        if (!Available) return;
        var original = await OriginalAsync();

        await _manager.CreateLinkedCloneAsync(original, "Copie");
        var second = await _manager.CreateLinkedCloneAsync(original, "Copie 2");

        Assert.Equal(
            new[] { "disk0-Base de Copie 2.qcow2", "disk0-Base de Copie.qcow2", "disk0.qcow2" },
            original.Disks[0].ChainFromTop);
        Assert.Equal(@"..\Debian\disk0-Base de Copie.qcow2", second.Disks[0].LinkedBaseFileName);
        Assert.True(await Holds(Top(second), "0x11", "0"));
    }

    [Fact]
    public async Task A_clone_name_outside_ascii_still_gives_an_ascii_snapshot_name()
    {
        if (!Available) return;
        var original = await OriginalAsync();

        await _manager.CreateLinkedCloneAsync(original, "Débian été");

        Assert.Equal("disk0-Base de Debian ete.qcow2", original.Disks[0].FileName);
    }

    [Fact]
    public async Task A_full_copy_of_a_linked_clone_depends_on_nothing()
    {
        if (!Available) return;
        var original = await OriginalAsync();
        var linked = await _manager.CreateLinkedCloneAsync(original, "Copie");
        await Io(Top(linked), "write -P 0x22 64k 64k");

        var full = await _manager.CloneVirtualMachineAsync(linked, "Indépendante");

        Assert.False(full.Disks[0].IsLinkedClone);
        Assert.Null((await _images!.GetInfoAsync(Top(full))).BackingFile);
        Assert.True(await Holds(Top(full), "0x11", "0"));
        Assert.True(await Holds(Top(full), "0x22", "64k"));
    }

    [Fact]
    public async Task A_raw_disk_is_refused_before_anything_changes()
    {
        if (!Available) return;
        var original = await OriginalAsync();
        await _images!.CreateAsync(Path.Combine(original.FolderPath, "data.img"), DiskFormat.Raw, SmallDisk);
        original.Disks.Add(new DiskConfig { Id = "disk1", FileName = "data.img", Format = DiskFormat.Raw });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.CreateLinkedCloneAsync(original, "Copie"));

        Assert.Equal("disk0.qcow2", original.Disks[0].FileName);
        Assert.False(Directory.Exists(Path.Combine(_root, "Copie")));
        Assert.Single(Directory.GetFiles(original.FolderPath, "disk0*"));
    }

    /// <summary>
    /// The original takes a live snapshot "Avant" (0x22 written after it), then the clone is made, then
    /// the original writes 0x33. The clone reads disk0-Avant.qcow2: 0x11, 0x22, nothing at 128k.
    /// </summary>
    private async Task<(VmConfig Original, VmConfig Clone)> CloneAfterSnapshotAsync()
    {
        var original = await OriginalAsync();
        await _images!.CreateAsync(Path.Combine(original.FolderPath, "disk0-Avant.qcow2"), DiskFormat.Qcow2, SmallDisk, "disk0.qcow2");
        original.Disks[0].PushOverlay("disk0-Avant.qcow2");
        _manager.Save(original);
        await Io(Top(original), "write -P 0x22 64k 64k");

        var clone = await _manager.CreateLinkedCloneAsync(original, "Copie");
        await Io(Top(original), "write -P 0x33 128k 64k");
        return (original, clone);
    }

    private async Task<bool> CloneIsIntactAsync(VmConfig clone) =>
        await Holds(Top(clone), "0x11", "0")
        && await Holds(Top(clone), "0x22", "64k")
        && await Holds(Top(clone), "0x00", "128k");

    [Fact]
    public async Task Restoring_a_snapshot_the_clone_rests_on_branches_and_keeps_its_images()
    {
        if (!Available) return;
        var (original, clone) = await CloneAfterSnapshotAsync();

        await new LiveSnapshotOperations(_images!).RevertAsync(original, "Avant", _manager.ProtectedImages);

        // The original is back to the state of "Avant", on a fresh layer: the old overlay is the clone's.
        Assert.Equal(new[] { "disk0-Avant (2).qcow2", "disk0.qcow2" }, original.Disks[0].ChainFromTop);
        Assert.True(await Holds(Top(original), "0x11", "0"));
        Assert.True(await Holds(Top(original), "0x00", "64k"));
        Assert.True(await Holds(Top(original), "0x00", "128k"));

        // What the original wrote after the clone was not anyone's base: gone, as in a plain restore.
        Assert.False(File.Exists(Path.Combine(original.FolderPath, "disk0-Base de Copie.qcow2")));
        var retained = Assert.Single(original.Disks[0].RetainedImages);
        Assert.Equal("disk0-Avant.qcow2", retained.FileName);
        Assert.Equal("disk0.qcow2", retained.BackingFileName);

        Assert.True(await CloneIsIntactAsync(clone));

        // disk0 now carries both the original's new layer and the clone's kept one: merging into it stays refused.
        Assert.NotNull(_manager.WhyLiveSnapshotCannotBeDeleted(original, "Avant (2)"));
        Assert.NotNull(_manager.WhyFilesCannotBeDeleted(original));
    }

    [Fact]
    public async Task Restoring_the_layer_frozen_for_the_clone_keeps_its_name()
    {
        if (!Available) return;
        var (original, clone) = await CloneAfterSnapshotAsync();

        await new LiveSnapshotOperations(_images!).RevertAsync(original, "Base de Copie", _manager.ProtectedImages);

        Assert.Equal(new[] { "disk0-Base de Copie.qcow2", "disk0-Avant.qcow2", "disk0.qcow2" }, original.Disks[0].ChainFromTop);
        Assert.Empty(original.Disks[0].RetainedImages);
        Assert.True(await Holds(Top(original), "0x22", "64k"));
        Assert.True(await Holds(Top(original), "0x00", "128k"));   // Written since: discarded.
        Assert.True(await CloneIsIntactAsync(clone));
    }

    [Fact]
    public async Task Reverting_the_top_overlay_of_a_disk_branches_the_same_way()
    {
        if (!Available) return;
        var (original, clone) = await CloneAfterSnapshotAsync();

        await new LiveSnapshotOperations(_images!).RevertTopAsync(original, original.Disks[0], _manager.ProtectedImages);

        Assert.Equal("disk0-Base de Copie.qcow2", original.Disks[0].FileName);
        Assert.True(await Holds(Top(original), "0x00", "128k"));
        Assert.True(await CloneIsIntactAsync(clone));
    }

    [Fact]
    public async Task Deleting_the_clone_releases_the_images_kept_for_it()
    {
        if (!Available) return;
        var (original, clone) = await CloneAfterSnapshotAsync();
        await new LiveSnapshotOperations(_images!).RevertAsync(original, "Avant", _manager.ProtectedImages);
        _manager.Save(original);

        await _manager.DeleteVirtualMachineAsync(clone, deleteFiles: true);

        Assert.Empty(original.Disks[0].RetainedImages);
        Assert.False(File.Exists(Path.Combine(original.FolderPath, "disk0-Avant.qcow2")));
        Assert.Null(_manager.WhyLiveSnapshotCannotBeDeleted(original, "Avant (2)"));
        Assert.True(await Holds(Top(original), "0x11", "0"));

        // Saved, not only changed in memory.
        _manager.ReloadLibrary();
        Assert.Empty(_manager.VirtualMachines.Single(m => m.Id == original.Id).Disks[0].RetainedImages);
    }

    [Fact]
    public async Task Removing_the_clone_from_the_list_releases_nothing()
    {
        if (!Available) return;
        var (original, clone) = await CloneAfterSnapshotAsync();
        await new LiveSnapshotOperations(_images!).RevertAsync(original, "Avant", _manager.ProtectedImages);

        // Only forgotten: the clone is still on disk and may be added back.
        await _manager.DeleteVirtualMachineAsync(clone, deleteFiles: false);

        Assert.Single(original.Disks[0].RetainedImages);
        Assert.True(File.Exists(Path.Combine(original.FolderPath, "disk0-Avant.qcow2")));
    }
}
