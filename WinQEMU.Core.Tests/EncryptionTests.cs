using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>How an encrypted machine's command line names its password: a pipe, never the password.</summary>
public class EncryptionArgumentTests
{
    [Fact]
    public void Each_encrypted_level_of_a_chain_gets_its_key()
    {
        Assert.Equal(",encrypt.key-secret=s,backing.backing.encrypt.key-secret=s",
            DiskKeyArguments.KeyOptions(new[] { true, false, true }, "s"));
        Assert.Equal(string.Empty, DiskKeyArguments.KeyOptions(new[] { false }, "s"));
    }

    [Fact]
    public void The_drive_reads_its_key_from_the_pipe_secret()
    {
        var vm = Fixtures.Vm();
        vm.DisksEncrypted = true;
        var key = new DiskKeyArguments(@"\\.\pipe\winqemu-0123", new Dictionary<string, IReadOnlyList<bool>> { ["disk0"] = new[] { true, true } });

        var args = QemuArgumentBuilder.Build(vm, Fixtures.Installation(), 4444, diskKey: key);

        Assert.Equal($@"secret,id={QemuArgumentBuilder.DiskKeySecretId},file=\\.\pipe\winqemu-0123", Fixtures.Value(args, "-object"));
        var drive = Fixtures.Drive(args, "disk0")!;
        Assert.Contains(",encrypt.key-secret=wq-disk-key,backing.encrypt.key-secret=wq-disk-key", drive);
        Assert.True(Fixtures.IndexOf(args, "-object") < Fixtures.IndexOfContaining(args, "id=disk0,"));
    }

    [Fact]
    public void A_clear_machine_has_no_secret()
    {
        var args = Fixtures.Build(Fixtures.Vm());
        Assert.DoesNotContain(args, a => a.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("encrypt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_password_must_be_typed_twice_alike()
    {
        Assert.NotNull(DiskKey.WhyPasswordIsRefused("", ""));
        Assert.NotNull(DiskKey.WhyPasswordIsRefused("abc", "abd"));
        Assert.Null(DiskKey.WhyPasswordIsRefused("abc", "abc"));
    }
}

/// <summary>
/// Encrypted images against the real qemu-img and qemu-system (skipped without QEMU), keyslots made cheap
/// (iter-time 10 ms) so the suite does not spend 2 s per open.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EncryptionTests : IAsyncLifetime
{
    private const string Password = "Pâte à crêpes 42!";
    private static readonly DiskKey Key = new(Password) { IterationTimeMs = 10 };
    private const long Size = 16L * 1024 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-enc-" + Guid.NewGuid().ToString("N"));
    private QemuInstallation? _installation;
    private QemuImgService? _clear;
    private string _qemuIo = string.Empty;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _installation = (await QemuLocator.DetectAsync()).Installation;
        if (_installation is not null)
        {
            _clear = new QemuImgService(_installation);
            _qemuIo = Path.Combine(Path.GetDirectoryName(_installation.QemuImgPath)!, "qemu-io.exe");
        }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
        return Task.CompletedTask;
    }

    private bool Available => _clear is not null && File.Exists(_qemuIo);
    private QemuImgService Keyed => _clear!.WithKey(Key);
    private string At(string file) => Path.Combine(_folder, file);

    /// <summary>qemu-io on an image and its chain, the password in a file of the test's own folder.</summary>
    private async Task<string> IoAsync(string file, string command, DiskKey? key = null)
    {
        var secret = At("io-key.txt");
        await File.WriteAllBytesAsync(secret, System.Text.Encoding.UTF8.GetBytes((key ?? Key).Password));
        var levels = await _clear!.GetEncryptedLevelsAsync(At(file));
        var spec = $"driver=qcow2,file.filename={At(file)}" + DiskKeyArguments.KeyOptions(levels, "k");
        var result = await ProcessRunner.RunAsync(_qemuIo, new[] { "--object", $"secret,id=k,file={secret}", "--image-opts", "-c", command, spec });
        File.Delete(secret);
        return result.StandardOutput + result.StandardError;
    }

    private async Task<bool> HoldsAsync(string file, string pattern, string offset)
    {
        var output = await IoAsync(file, $"read -P {pattern} {offset} 64k");
        return output.Contains("read 65536/65536", StringComparison.Ordinal) && !output.Contains("verification failed", StringComparison.OrdinalIgnoreCase);
    }

    private void AssertNoPassword(QemuImgService images) =>
        Assert.DoesNotContain(images.LastArguments!, a => a.Contains(Password, StringComparison.Ordinal) || a.Contains("Pâte", StringComparison.Ordinal));

    /// <summary>An encrypted disk0.qcow2 with 0x11 at 0, in a machine of the test folder.</summary>
    private async Task<VmConfig> EncryptedMachineAsync()
    {
        var vm = Fixtures.Vm();
        vm.FolderPath = _folder;
        vm.DisksEncrypted = true;
        await Keyed.CreateAsync(At("disk0.qcow2"), DiskFormat.Qcow2, Size);
        await IoAsync("disk0.qcow2", "write -P 0x11 0 64k");
        return vm;
    }

    [Fact]
    public async Task A_keyed_service_creates_encrypted_images_and_never_passes_the_password()
    {
        if (!Available) return;
        var keyed = Keyed;
        await keyed.CreateAsync(At("a.qcow2"), DiskFormat.Qcow2, Size);
        AssertNoPassword(keyed);
        Assert.Contains(keyed.LastArguments!, a => a.StartsWith(@"secret,id=wq-key,file=\\.\pipe\winqemu-", StringComparison.Ordinal));

        Assert.True(await _clear!.IsEncryptedAsync(At("a.qcow2")));
        await keyed.VerifyKeyAsync(At("a.qcow2"));
        AssertNoPassword(keyed);

        var wrong = _clear.WithKey(new DiskKey("nope"));
        var refused = await Assert.ThrowsAsync<DiskPasswordException>(() => wrong.VerifyKeyAsync(At("a.qcow2")));
        Assert.True(refused.IsWrongPassword);

        // Without a password: said as such, not as a qemu-img failure some callers ignore.
        var locked = await Assert.ThrowsAsync<DiskPasswordException>(() => _clear.CreateOfflineSnapshotAsync(At("a.qcow2"), "x"));
        Assert.False(locked.IsWrongPassword);

        // Nothing but qcow2 can be encrypted, so nothing else is created for an encrypted machine.
        await Assert.ThrowsAsync<InvalidOperationException>(() => keyed.CreateAsync(At("b.img"), DiskFormat.Raw, Size));
    }

    [Fact]
    public async Task Offline_snapshots_work_on_encrypted_images()
    {
        if (!Available) return;
        await EncryptedMachineAsync();
        var keyed = Keyed;

        await keyed.CreateOfflineSnapshotAsync(At("disk0.qcow2"), "Avant");
        await IoAsync("disk0.qcow2", "write -P 0x22 0 64k");
        // Listed without the password: qemu-img info reads the header only.
        Assert.Contains((await _clear!.GetInfoAsync(At("disk0.qcow2"))).Snapshots, s => s.Name == "Avant");

        await keyed.ApplyOfflineSnapshotAsync(At("disk0.qcow2"), "Avant");
        Assert.True(await HoldsAsync("disk0.qcow2", "0x11", "0"));

        await keyed.DeleteOfflineSnapshotAsync(At("disk0.qcow2"), "Avant");
        Assert.Empty((await _clear.GetInfoAsync(At("disk0.qcow2"))).Snapshots);
        AssertNoPassword(keyed);
    }

    [Fact]
    public async Task Live_snapshot_levels_are_encrypted_and_revert_and_merge_with_the_key()
    {
        if (!Available) return;
        var vm = await EncryptedMachineAsync();
        var disk = vm.Disks[0];
        var keyed = Keyed;

        await keyed.CreateAsync(At("disk0-A.qcow2"), DiskFormat.Qcow2, Size, "disk0.qcow2");
        disk.PushOverlay("disk0-A.qcow2");
        await IoAsync("disk0-A.qcow2", "write -P 0x22 64k 64k");
        await keyed.CreateAsync(At("disk0-B.qcow2"), DiskFormat.Qcow2, Size, "disk0-A.qcow2");
        disk.PushOverlay("disk0-B.qcow2");
        await IoAsync("disk0-B.qcow2", "write -P 0x33 128k 64k");
        Assert.Equal(new[] { true, true, true }, await _clear!.GetEncryptedLevelsAsync(At("disk0-B.qcow2")));

        // Delete A keeping the data: commit into disk0, then rebase -u of B (the top's key only).
        await new LiveSnapshotOperations(keyed).MergeAsync(vm, "A");
        Assert.Equal(new[] { "disk0-B.qcow2", "disk0.qcow2" }, disk.ChainFromTop);
        Assert.True(await HoldsAsync("disk0-B.qcow2", "0x11", "0"));
        Assert.True(await HoldsAsync("disk0-B.qcow2", "0x22", "64k"));
        Assert.True(await HoldsAsync("disk0-B.qcow2", "0x33", "128k"));

        await new LiveSnapshotOperations(keyed).RevertAsync(vm, "B");
        Assert.Equal("disk0.qcow2", disk.FileName);
        Assert.True(await HoldsAsync("disk0.qcow2", "0x22", "64k"));
        Assert.True(await HoldsAsync("disk0.qcow2", "0x00", "128k"));
        AssertNoPassword(keyed);
    }

    [Fact]
    public async Task Encrypting_then_decrypting_a_machine_keeps_its_data_and_drops_the_clear_images()
    {
        if (!Available) return;
        var vm = Fixtures.Vm();
        vm.FolderPath = _folder;
        await _clear!.CreateAsync(At("disk0.qcow2"), DiskFormat.Qcow2, Size);
        await ProcessRunner.RunAsync(_qemuIo, new[] { "-c", "write -P 0x41 0 64k", At("disk0.qcow2") });
        await _clear.CreateAsync(At("disk0-S.qcow2"), DiskFormat.Qcow2, Size, "disk0.qcow2");
        vm.Disks[0].PushOverlay("disk0-S.qcow2");
        await ProcessRunner.RunAsync(_qemuIo, new[] { "-c", "write -P 0x42 64k 64k", At("disk0-S.qcow2") });
        Assert.Null(DiskEncryption.WhyCannotEncrypt(vm, _ => false));
        var saves = 0;

        var result = await DiskEncryption.RewriteAsync(vm, Keyed, encrypt: true, () => saves++);

        Assert.Empty(result.ImagesLeftInPlace);
        Assert.True(vm.DisksEncrypted);
        Assert.Equal("disk0.qcow2", vm.Disks[0].FileName);
        Assert.False(vm.Disks[0].IsOverlay);
        Assert.False(File.Exists(At("disk0-S.qcow2")));
        Assert.True(saves >= 2);
        Assert.Equal(new[] { true }, await _clear.GetEncryptedLevelsAsync(At("disk0.qcow2")));
        Assert.True(await HoldsAsync("disk0.qcow2", "0x41", "0"));
        Assert.True(await HoldsAsync("disk0.qcow2", "0x42", "64k"));
        // The pattern is nowhere in the file in clear.
        var bytes = await File.ReadAllBytesAsync(At("disk0.qcow2"));
        Assert.DoesNotContain(Enumerable.Range(0, bytes.Length - 64), i => bytes.AsSpan(i, 64).IndexOfAnyExcept((byte)0x41) < 0);
        Assert.NotNull(DiskEncryption.WhyCannotEncrypt(vm, _ => false));

        await DiskEncryption.RewriteAsync(vm, Keyed, encrypt: false, () => { });

        Assert.False(vm.DisksEncrypted);
        Assert.Equal(new[] { false }, await _clear.GetEncryptedLevelsAsync(At("disk0.qcow2")));
        var clear = await ProcessRunner.RunAsync(_qemuIo, new[] { "-r", "-c", "read -P 0x42 64k 64k", At("disk0.qcow2") });
        Assert.DoesNotContain("verification failed", clear.StandardOutput + clear.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(_folder, "*.qcow2"));
    }

    [Fact]
    public async Task Changing_the_password_rekeys_every_level_and_keeps_snapshots()
    {
        if (!Available) return;
        var vm = await EncryptedMachineAsync();
        await Keyed.CreateOfflineSnapshotAsync(At("disk0.qcow2"), "Garde");
        await Keyed.CreateAsync(At("disk0-A.qcow2"), DiskFormat.Qcow2, Size, "disk0.qcow2");
        vm.Disks[0].PushOverlay("disk0-A.qcow2");
        var renewed = new DiskKey("nouveau mot de passe") { IterationTimeMs = 10 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => DiskEncryption.ChangePasswordAsync(vm, Keyed, new DiskKey(Password)));
        await DiskEncryption.ChangePasswordAsync(vm, Keyed, renewed);

        await _clear!.WithKey(renewed).VerifyKeyAsync(At("disk0-A.qcow2"));
        await _clear.WithKey(renewed).VerifyKeyAsync(At("disk0.qcow2"));
        await Assert.ThrowsAsync<DiskPasswordException>(() => Keyed.VerifyKeyAsync(At("disk0.qcow2")));
        Assert.Contains((await _clear.GetInfoAsync(At("disk0.qcow2"))).Snapshots, s => s.Name == "Garde");
        Assert.True(await HoldsAsync("disk0-A.qcow2", "0x11", "0", renewed));
    }

    private async Task<bool> HoldsAsync(string file, string pattern, string offset, DiskKey key)
    {
        var output = await IoAsync(file, $"read -P {pattern} {offset} 64k", key);
        return output.Contains("read 65536/65536", StringComparison.Ordinal) && !output.Contains("verification failed", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clones_and_compaction_stay_encrypted()
    {
        if (!Available) return;
        await EncryptedMachineAsync();
        await Keyed.ConvertAsync(At("disk0.qcow2"), At("copy.qcow2"), DiskFormat.Qcow2);
        Assert.True(await _clear!.IsEncryptedAsync(At("copy.qcow2")));
        Assert.True(await HoldsAsync("copy.qcow2", "0x11", "0"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Keyed.ConvertAsync(At("disk0.qcow2"), At("c.qcow2"), DiskFormat.Qcow2, compress: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Keyed.ConvertAsync(At("disk0.qcow2"), At("c.img"), DiskFormat.Raw));

        await DiskCompaction.CompactAsync(Keyed, At("copy.qcow2"));
        Assert.True(await _clear.IsEncryptedAsync(At("copy.qcow2")));

        // The OVA's VMDK is the one clear copy, on purpose (the export dialog says so).
        await Keyed.ConvertToStreamOptimizedVmdkAsync(At("disk0.qcow2"), At("disk0.vmdk"), "ide");
        Assert.False(await _clear.IsEncryptedAsync(At("disk0.vmdk")));
    }

    [Fact]
    public async Task The_credential_manager_keeps_and_forgets_a_password()
    {
        var id = Guid.NewGuid();
        try
        {
            Assert.Null(DiskPasswordStore.Read(id));
            DiskPasswordStore.Save(id, Password, "WinQEMU test");
            Assert.Equal(Password, DiskPasswordStore.Read(id));
            Assert.True(DiskPasswordStore.Exists(id));
        }
        finally
        {
            DiskPasswordStore.Delete(id);
        }
        Assert.False(DiskPasswordStore.Exists(id));
        DiskPasswordStore.Delete(id);   // Forgetting twice is harmless.
        await Task.CompletedTask;
    }

    /// <summary>
    /// The whole path through a real QEMU: the session serves the password on a pipe, QEMU opens the encrypted disk,
    /// a live snapshot gives it an encrypted overlay, and after power-off the chain opens with the key, the base intact.
    /// </summary>
    [Fact]
    public async Task A_running_machine_opens_its_encrypted_disk_and_snapshots_it_encrypted()
    {
        if (!Available || !_installation!.SupportsArchitecture(GuestArchitecture.X86_64)) return;
        var vm = await EncryptedMachineAsync();
        vm.Firmware = FirmwareType.Bios;
        vm.Accelerator = AcceleratorType.Tcg;
        vm.MemoryMb = 128;
        vm.EnableClipboard = false;
        vm.EnableAudio = false;
        vm.EnableBalloon = false;
        // No window: the last -display wins.
        vm.ExtraArguments = "-display none -S";

        var session = new VmSession(vm, _installation);
        await Assert.ThrowsAsync<DiskPasswordException>(() => session.StartAsync());

        session = new VmSession(vm, _installation) { DiskKey = Key };
        try
        {
            await session.StartAsync();
            Assert.Equal(VmState.Running, session.State);
            Assert.Contains(@"\\.\pipe\winqemu-", session.LaunchCommandLine);
            Assert.DoesNotContain(Password, session.LaunchCommandLine);

            var overlays = await session.CreateLiveDiskSnapshotAsync("Vivant");
            vm.Disks[0].PushOverlay(Path.GetFileName(overlays["disk0"]));
        }
        finally
        {
            await session.PowerOffAsync();
            await session.DisposeAsync();
        }

        Assert.Equal(new[] { true, true }, await _clear!.GetEncryptedLevelsAsync(At("disk0-Vivant.qcow2")));
        Assert.True(await HoldsAsync("disk0-Vivant.qcow2", "0x11", "0"));

        // Wrong password: QEMU refuses, said as such.
        var wrong = new VmSession(vm, _installation) { DiskKey = new DiskKey("nope") };
        try
        {
            var refused = await Assert.ThrowsAsync<DiskPasswordException>(() => wrong.StartAsync());
            Assert.True(refused.IsWrongPassword);
        }
        finally
        {
            await wrong.DisposeAsync();
        }
    }
}

/// <summary>The manager's side: unlocking through the prompt, creating, cloning (full and linked) an encrypted machine.</summary>
[Trait("Category", "Integration")]
public sealed class EncryptedMachineTests : IAsyncLifetime
{
    private const string Password = "secret de test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winqemu-encvm-" + Guid.NewGuid().ToString("N"));
    private VmManager _manager = null!;
    private QemuImgService? _clear;
    private readonly List<Guid> _created = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _manager = new VmManager(new AppSettings { LibraryRoot = _root });
        await _manager.InitializeAsync();
        _clear = _manager.DiskService;
    }

    public async Task DisposeAsync()
    {
        // Nothing may stay in the Credential Manager.
        foreach (var id in _created.Concat(_manager.VirtualMachines.Select(m => m.Id)))
            DiskPasswordStore.Delete(id);
        await _manager.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private async Task<VmConfig> CreateAsync(bool remember)
    {
        var vm = await _manager.CreateVirtualMachineAsync(new CreateVmRequest
        {
            Name = "Secret",
            DiskCapacityBytes = 16L * 1024 * 1024,
            NetworkMode = NetworkMode.None,
            DiskKey = new DiskKey(Password) { IterationTimeMs = 10 },
            RememberDiskPassword = remember
        });
        _created.Add(vm.Id);
        return vm;
    }

    [Fact]
    public async Task The_prompt_is_asked_again_after_a_wrong_password_and_cancel_stops()
    {
        if (_clear is null) return;
        var vm = await CreateAsync(remember: false);
        Assert.True(vm.DisksEncrypted);
        Assert.True(await _clear.IsEncryptedAsync(vm.ResolvePath(vm.Disks[0].FileName)));
        Assert.False(_manager.IsDiskPasswordRemembered(vm));
        Assert.True(_manager.IsUnlocked(vm));   // Given in the wizard: kept for this run.

        // A new run of WinQEMU: nothing in memory.
        var fresh = new VmManager(new AppSettings { LibraryRoot = _root });
        await fresh.InitializeAsync();
        var machine = Assert.Single(fresh.VirtualMachines);
        Assert.Throws<DiskPasswordException>(() => fresh.ImagesFor(machine));
        Assert.Null(await fresh.UnlockAsync(machine, interactive: false));

        var problems = new List<string?>();
        var answers = new Queue<DiskPasswordAnswer?>(new[] { new DiskPasswordAnswer("faux", false), new DiskPasswordAnswer(Password, false) });
        fresh.DiskPasswordPrompt = (_, problem) => { problems.Add(problem); return Task.FromResult(answers.Dequeue()); };
        Assert.NotNull(await fresh.UnlockAsync(machine));
        Assert.Equal(2, problems.Count);
        Assert.Null(problems[0]);
        Assert.NotNull(problems[1]);
        Assert.NotNull(fresh.ImagesFor(machine).Key);
        Assert.False(fresh.IsDiskPasswordRemembered(machine));

        var other = new VmManager(new AppSettings { LibraryRoot = _root });
        await other.InitializeAsync();
        other.DiskPasswordPrompt = (_, _) => Task.FromResult<DiskPasswordAnswer?>(null);
        await Assert.ThrowsAsync<DiskUnlockCanceledException>(() => other.UnlockAsync(other.VirtualMachines[0]));
        await Assert.ThrowsAsync<DiskPasswordException>(() => other.UnlockWithPasswordAsync(other.VirtualMachines[0], "faux"));
        Assert.NotNull(await other.UnlockWithPasswordAsync(other.VirtualMachines[0], Password));
        await fresh.DisposeAsync();
        await other.DisposeAsync();
    }

    [Fact]
    public async Task A_remembered_password_unlocks_without_asking_until_forgotten()
    {
        if (_clear is null) return;
        var vm = await CreateAsync(remember: true);
        Assert.True(_manager.IsDiskPasswordRemembered(vm));

        var fresh = new VmManager(new AppSettings { LibraryRoot = _root });
        await fresh.InitializeAsync();
        var machine = Assert.Single(fresh.VirtualMachines);
        Assert.NotNull(await fresh.UnlockAsync(machine, interactive: false));

        fresh.ForgetDiskPassword(machine);
        Assert.False(fresh.IsDiskPasswordRemembered(machine));
        Assert.Null(await fresh.UnlockAsync(machine, interactive: false));
        await fresh.DisposeAsync();
    }

    [Fact]
    public async Task Clones_of_an_encrypted_machine_are_encrypted_with_its_password()
    {
        if (_clear is null) return;
        var vm = await CreateAsync(remember: false);

        var copy = await _manager.CloneVirtualMachineAsync(vm, "Copie");
        _created.Add(copy.Id);
        Assert.True(copy.DisksEncrypted);
        Assert.True(await _clear.IsEncryptedAsync(copy.ResolvePath(copy.Disks[0].FileName)));
        Assert.True(_manager.IsUnlocked(copy));

        var linked = await _manager.CreateLinkedCloneAsync(vm, "Liee");
        _created.Add(linked.Id);
        Assert.Equal(new[] { true, true }, await _clear.GetEncryptedLevelsAsync(linked.ResolvePath(linked.Disks[0].FileName)));
        Assert.Equal(new[] { true, true }, await _clear.GetEncryptedLevelsAsync(vm.ResolvePath(vm.Disks[0].FileName)));
        await _manager.ImagesFor(linked).VerifyKeyAsync(linked.ResolvePath(linked.Disks[0].FileName));

        // Linked: one password for both, so neither may change it or rewrite its disks.
        Assert.NotNull(_manager.WhyCannotChangeDiskPassword(vm));
        Assert.NotNull(_manager.WhyCannotChangeDiskPassword(linked));
        Assert.NotNull(_manager.WhyCannotDecrypt(linked));
    }
}
