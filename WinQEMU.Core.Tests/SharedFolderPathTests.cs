using WinQEMU.Core.Models;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>
/// A shared folder's row says where the guest finds it: a guest set up while WinQEMU was in French keeps
/// ~/Partages after a switch to English, and the row has to say so rather than ~/Shares.
/// </summary>
public sealed class SharedFolderPathTests
{
    [Fact]
    public void The_folder_the_guest_reported_is_shown_for_every_share_old_and_new()
    {
        var vm = new VmConfig();
        vm.SharedFolders.Add(new SharedFolderConfig { HostPath = @"C:\Users\x\Music", Name = "Music" });
        Assert.Equal($"~/{GuestShareMounter.GuestFolder}/Music", vm.SharedFolders[0].GuestPath);

        vm.GuestSharesFolder = "Partages";
        vm.SharedFolders.Add(new SharedFolderConfig { HostPath = @"C:\Users\x\Pictures", Name = "Pictures" });

        Assert.Equal(new[] { "~/Partages/Music", "~/Partages/Pictures" }, vm.SharedFolders.Select(f => f.GuestPath));
    }

    [Fact]
    public void A_descriptor_read_back_shows_the_folder_it_recorded()
    {
        var vm = new VmConfig { GuestSharesFolder = "Shares" };
        vm.SharedFolders.Add(new SharedFolderConfig { HostPath = @"C:\data", Name = "data" });
        var json = System.Text.Json.JsonSerializer.Serialize(vm);

        var back = System.Text.Json.JsonSerializer.Deserialize<VmConfig>(json)!;

        Assert.Equal("~/Shares/data", back.SharedFolders[0].GuestPath);
    }
}
