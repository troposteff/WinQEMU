using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>Descriptors and settings written whole or not at all, and a library that survives what it finds on disk.</summary>
public sealed class SafeFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"wq-safe-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_rewrite_keeps_the_previous_version_and_a_damaged_file_reads_from_it()
    {
        var path = Path.Combine(_folder, "settings.json");
        SafeFile.WriteAllText(path, "{\"a\":1}");
        SafeFile.WriteAllText(path, "{\"a\":2}");

        Assert.Equal("{\"a\":2}", File.ReadAllText(path));
        Assert.Equal("{\"a\":1}", File.ReadAllText(SafeFile.BackupOf(path)));
        Assert.False(File.Exists(path + ".tmp"));

        // A power cut while an older version wrote it: zeros.
        File.WriteAllBytes(path, new byte[64]);
        Assert.Equal("{\"a\":1}", SafeFile.ReadValid(path, text => text.StartsWith('{')));
    }

    [Fact]
    public void A_machine_folder_copied_in_Explorer_becomes_a_machine_of_its_own()
    {
        var library = new VmLibrary(_folder, null);
        var original = new VmConfig { Name = "Debian", FolderPath = Path.Combine(_folder, "Debian") };
        original.NetworkAdapters.Add(new NetworkAdapterConfig { Id = "net0", MacAddress = "52:54:00:12:34:56" });
        library.Save(original);
        // Older creation date for the original, as a copy made afterwards would have.
        Directory.SetCreationTimeUtc(original.FolderPath, DateTime.UtcNow.AddDays(-1));
        var copyFolder = Directory.CreateDirectory(Path.Combine(_folder, "Debian - Copie")).FullName;
        File.Copy(original.DescriptorPath, Path.Combine(copyFolder, Path.GetFileName(original.DescriptorPath)));

        var machines = library.Scan(out var warnings);

        Assert.Equal(2, machines.Count);
        Assert.Equal(original.Id, machines.Single(m => m.FolderPath == original.FolderPath).Id);
        var copy = machines.Single(m => m.FolderPath == copyFolder);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.NotEqual("52:54:00:12:34:56", copy.NetworkAdapters[0].MacAddress);
        Assert.Single(warnings);
        // Saved: the next scan finds two machines without a warning.
        library.Scan(out var again);
        Assert.Empty(again);
    }
}
