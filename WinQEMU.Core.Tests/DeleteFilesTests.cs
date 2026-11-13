using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>"Delete with files" deletes the machine's folder whole: never when it holds more than the machine.</summary>
public sealed class DeleteFilesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winqemu-delete-" + Guid.NewGuid().ToString("N"));
    private VmManager _manager = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _manager = new VmManager(new AppSettings { LibraryRoot = _root });
        await _manager.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private VmConfig Machine(string name, string folder)
    {
        var config = new VmConfig { Name = name, FolderPath = folder };
        _manager.Library!.Save(config);
        return config;
    }

    private async Task<VmConfig> Loaded(string name)
    {
        await _manager.InitializeAsync();
        return _manager.VirtualMachines.Single(m => m.Name == name);
    }

    [Fact]
    public async Task A_folder_holding_another_machine_is_not_deleted()
    {
        Machine("Debian", Path.Combine(_root, "Debian"));
        Machine("Copie", Path.Combine(_root, "Debian", "Copie"));

        Assert.NotNull(_manager.WhyFilesCannotBeDeleted(await Loaded("Debian")));
        Assert.Null(_manager.WhyFilesCannotBeDeleted(await Loaded("Copie")));
    }

    [Fact]
    public async Task A_folder_holding_a_disk_of_another_machine_is_not_deleted()
    {
        var debian = Machine("Debian", Path.Combine(_root, "Debian"));
        var other = new VmConfig { Name = "Autre", FolderPath = Path.Combine(_root, "Autre") };
        other.Disks.Add(new DiskConfig { Id = "disk1", FileName = Path.Combine(debian.FolderPath, "data.qcow2") });
        _manager.Library!.Save(other);

        Assert.Contains("Autre", _manager.WhyFilesCannotBeDeleted(await Loaded("Debian")));
    }

    [Fact]
    public async Task A_machine_at_the_library_root_does_not_take_the_library_with_it()
    {
        Machine("Racine", _root);

        Assert.NotNull(_manager.WhyFilesCannotBeDeleted(await Loaded("Racine")));
    }

    [Fact]
    public void Remove_from_list_takes_the_descriptor_even_under_a_name_never_saved()
    {
        var debian = Machine("Debian", Path.Combine(_root, "Debian"));
        debian.Name = "Debian: essai";   // refused by ValidateName, so never written

        _manager.Library!.Delete(debian, deleteFiles: false);

        Assert.Empty(Directory.GetFiles(debian.FolderPath, "*.wqvm"));
    }

    [Fact]
    public void A_folder_name_with_a_path_separator_is_refused()
    {
        var tree = new LibraryTree();
        Assert.Equal(FolderProblem.Separator, tree.CheckName("Travail/Linux", null));
        Assert.Equal(FolderProblem.None, tree.CheckName("Travail - Linux", null));
    }
}
