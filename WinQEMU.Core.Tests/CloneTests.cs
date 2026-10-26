using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

public class CloneNamingTests
{
    [Theory]
    [InlineData("    (0.00/100%)", 0d)]
    [InlineData("    (12.34/100%)", 0.1234)]
    [InlineData("    (100.00/100%)", 1d)]
    public void Convert_progress_lines_are_read(string line, double expected)
    {
        Assert.Equal(expected, QemuImgService.ParseProgress(line)!.Value, precision: 6);
    }

    [Fact]
    public void Other_output_is_not_mistaken_for_progress()
    {
        Assert.Null(QemuImgService.ParseProgress("qemu-img: Could not open 'x.qcow2'"));
        Assert.Null(QemuImgService.ParseProgress(string.Empty));
    }

    [Fact]
    public void The_suggested_name_skips_names_already_in_the_library()
    {
        var manager = new VmManager(new AppSettings());
        var source = Fixtures.Vm("Debian");
        manager.VirtualMachines.Add(source);
        Assert.Equal("Debian (copie)", manager.SuggestCloneName(source));

        manager.VirtualMachines.Add(Fixtures.Vm("Debian (copie)"));
        manager.VirtualMachines.Add(Fixtures.Vm("debian (COPIE 2)"));
        Assert.Equal("Debian (copie 3)", manager.SuggestCloneName(source));
    }
}

/// <summary>
/// Cloning against the real qemu-img, checking the bytes the clone holds rather than which files exist.
/// Skipped when QEMU is not installed.
/// </summary>
[Trait("Category", "Integration")]
public class CloneTests : IAsyncLifetime
{
    private const long SmallDisk = 16L * 1024 * 1024;

    private string _root = string.Empty;
    private string _sourceFolder = string.Empty;
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

        _root = Path.Combine(Path.GetTempPath(), "winqemu-clone-" + Guid.NewGuid().ToString("N"));
        _sourceFolder = Path.Combine(_root, "Source");
        Directory.CreateDirectory(_sourceFolder);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private bool Available => _images is not null && File.Exists(_qemuIo);

    private VmLibrary Library => new(_root, _images);

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

    private string InSource(string file) => Path.Combine(_sourceFolder, file);

    /// <summary>
    /// disk0.qcow2 carrying an internal snapshot, under a live-snapshot overlay that names its base by
    /// absolute path — the way QEMU writes it — with 0x11 in the base and 0x22 in the overlay.
    /// </summary>
    private async Task<VmConfig> SourceWithOverlayAsync()
    {
        var vm = Fixtures.Vm("Source");
        vm.FolderPath = _sourceFolder;
        vm.NetworkAdapters.Add(new NetworkAdapterConfig { Id = "net0", MacAddress = "52:54:00:aa:bb:cc" });
        vm.NetworkAdapters.Add(new NetworkAdapterConfig { Id = "net1", MacAddress = string.Empty });

        await _images!.CreateAsync(InSource("disk0.qcow2"), DiskFormat.Qcow2, SmallDisk);
        await Io(InSource("disk0.qcow2"), "write -P 0x11 0 64k");
        await _images.CreateOfflineSnapshotAsync(InSource("disk0.qcow2"), "Avant");
        await _images.CreateAsync(InSource("disk0-Test.qcow2"), DiskFormat.Qcow2, SmallDisk, InSource("disk0.qcow2"));
        vm.Disks[0].PushOverlay("disk0-Test.qcow2");
        await Io(InSource("disk0-Test.qcow2"), "write -P 0x22 64k 64k");

        Library.Save(vm);
        return vm;
    }

    [Fact]
    public async Task The_clone_holds_the_current_contents_in_one_standalone_image()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();

        var clone = await Library.CloneAsync(source, "Copie");

        var disk = Assert.Single(clone.Disks);
        Assert.Equal("disk0.qcow2", disk.FileName);
        Assert.False(disk.IsOverlay);

        var path = clone.ResolvePath(disk.FileName);
        Assert.StartsWith(clone.FolderPath, path);
        Assert.True(await Holds(path, "0x11", "0"));
        Assert.True(await Holds(path, "0x22", "64k"));

        // No backing file: the clone no longer reads anything of the original's.
        var info = await _images!.GetInfoAsync(path);
        Assert.Null(info.BackingFile);
        Assert.Empty(info.Snapshots);
    }

    [Fact]
    public async Task Writing_to_the_original_afterwards_does_not_reach_the_clone()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();
        var clone = await Library.CloneAsync(source, "Copie");

        // What merging the original's snapshot does to the image the overlay sits on.
        await _images!.CommitAsync(InSource("disk0-Test.qcow2"));
        await Io(InSource("disk0.qcow2"), "write -P 0x44 0 64k");

        var path = clone.ResolvePath(clone.Disks[0].FileName);
        Assert.True(await Holds(path, "0x11", "0"));
    }

    [Fact]
    public async Task The_clone_gets_its_own_identity_and_leaves_the_original_untouched()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();

        var clone = await Library.CloneAsync(source, "Copie");

        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal("Copie", clone.Name);
        Assert.NotEqual(_sourceFolder, clone.FolderPath);
        Assert.True(File.Exists(clone.DescriptorPath));
        Assert.Null(clone.LastStartedUtc);

        Assert.NotEqual("52:54:00:aa:bb:cc", clone.NetworkAdapters[0].MacAddress);
        Assert.StartsWith("52:54:00:", clone.NetworkAdapters[0].MacAddress);
        Assert.Equal(string.Empty, clone.NetworkAdapters[1].MacAddress);

        // The hardware itself is the same machine.
        Assert.Equal(source.MemoryMb, clone.MemoryMb);
        Assert.Equal(source.Disks[0].Bus, clone.Disks[0].Bus);
        Assert.Equal(source.Disks[0].BootIndex, clone.Disks[0].BootIndex);

        Assert.Equal("disk0-Test.qcow2", source.Disks[0].FileName);
        Assert.Equal("52:54:00:aa:bb:cc", source.NetworkAdapters[0].MacAddress);

        // And the descriptor on disk reloads as two distinct machines.
        var machines = Library.Scan(out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(2, machines.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public async Task A_disk_attached_from_outside_the_folder_is_copied_not_shared()
    {
        if (!Available) return;
        var external = Path.Combine(_root, "external.qcow2");
        await _images!.CreateAsync(external, DiskFormat.Qcow2, SmallDisk);
        await Io(external, "write -P 0x55 0 64k");

        var source = Fixtures.Vm("Source");
        source.FolderPath = _sourceFolder;
        source.Disks[0].FileName = external;

        var clone = await Library.CloneAsync(source, "Copie");

        var path = clone.ResolvePath(clone.Disks[0].FileName);
        Assert.NotEqual(external, path);
        Assert.StartsWith(clone.FolderPath, path);
        Assert.True(await Holds(path, "0x55", "0"));
    }

    [Fact]
    public async Task The_uefi_variable_store_comes_along()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();
        await File.WriteAllBytesAsync(QemuArgumentBuilder.GetUefiVarsPath(source), new byte[] { 1, 2, 3 });

        var clone = await Library.CloneAsync(source, "Copie");

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(QemuArgumentBuilder.GetUefiVarsPath(clone)));
    }

    [Fact]
    public async Task Progress_rises_to_completion()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();
        var reports = new RecordingProgress();

        await Library.CloneAsync(source, "Copie", reports);

        Assert.NotEmpty(reports.Values);
        Assert.Equal(1d, reports.Values[^1]);
        Assert.Equal(reports.Values.OrderBy(v => v), reports.Values);
    }

    [Fact]
    public async Task A_missing_disk_fails_before_anything_is_created()
    {
        if (!Available) return;
        var source = Fixtures.Vm("Source");
        source.FolderPath = _sourceFolder;

        await Assert.ThrowsAsync<FileNotFoundException>(() => Library.CloneAsync(source, "Copie"));

        Assert.False(Directory.Exists(Path.Combine(_root, "Copie")));
    }

    [Fact]
    public async Task Cancelling_midway_removes_the_half_made_clone()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();
        await _images!.CreateAsync(InSource("disk1.qcow2"), DiskFormat.Qcow2, SmallDisk);
        source.Disks.Add(new DiskConfig { Id = "disk1", FileName = "disk1.qcow2" });

        // Cancelled once the first of the two disks is done, so the second copy never completes.
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress(value => { if (value >= 0.5) cancellation.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Library.CloneAsync(source, "Copie", progress, cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(_root, "Copie")));
        Assert.True(File.Exists(InSource("disk0-Test.qcow2")));
    }

    [Fact]
    public async Task An_unusable_name_is_refused()
    {
        if (!Available) return;
        var source = await SourceWithOverlayAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Library.CloneAsync(source, "CON"));
    }

    /// <summary>Records synchronously; <see cref="Progress{T}"/> would post to the thread pool and lose the order.</summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        private readonly Action<double>? _onReport;
        public RecordingProgress(Action<double>? onReport = null) => _onReport = onReport;
        public List<double> Values { get; } = new();

        public void Report(double value)
        {
            lock (Values)
                Values.Add(value);
            _onReport?.Invoke(value);
        }
    }
}
