using WinQEMU.Core.Models;
using Xunit;

namespace WinQEMU.Core.Tests;

public class GuestOsCatalogTests
{
    [Fact]
    public void Download_pages_are_https_pages_not_files()
    {
        foreach (var profile in GuestOsCatalog.Profiles.Where(p => p.DownloadPage is not null))
        {
            Assert.True(Uri.TryCreate(profile.DownloadPage, UriKind.Absolute, out var page), profile.Key);
            Assert.Equal(Uri.UriSchemeHttps, page!.Scheme);
            // A page that lists the current releases, never an ISO address that goes stale with the next one.
            Assert.False(page.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase), profile.Key);
        }
    }

    [Fact]
    public void Families_come_in_catalog_order() =>
        Assert.Equal(new[] { "Windows", "Linux", "BSD", "Other" }, GuestOsCatalog.Families);

    [Fact]
    public void Keys_are_unique() =>
        Assert.Equal(GuestOsCatalog.Profiles.Count, GuestOsCatalog.Profiles.Select(p => p.Key).Distinct().Count());

    [Theory]
    // A description as an OVF from elsewhere gives it: the derivative wins over what it is based on.
    [InlineData("Linux Mint 22 (Ubuntu based)", "linux-mint")]
    [InlineData("Kali GNU/Linux Rolling (Debian based)", "kali")]
    [InlineData("Rocky Linux 9", "rhel")]
    [InlineData("pfSense 2.8 (FreeBSD 15)", "pfsense")]
    [InlineData("Other 64-bit architecture", "other")]
    public void Descriptions_map_to_the_most_precise_profile(string description, string expected) =>
        Assert.Equal(expected, WinQEMU.Core.Import.GuestOsMapping.FromOvf(null, null, null, description));

    [Fact]
    public void Every_mapped_key_is_a_profile()
    {
        foreach (var id in new[] { "linux-mint", "pop-os", "zorin", "kali", "manjaro", "ubuntu", "debian", "fedora", "rhel",
                     "opensuse", "arch", "nixos", "alpine", "freebsd", "pfsense", "opnsense", "linux", "other" })
            Assert.NotNull(GuestOsCatalog.Find(id));
    }

    [Theory]
    [InlineData("windows-11")]
    [InlineData("ubuntu")]
    [InlineData("debian")]
    [InlineData("fedora")]
    [InlineData("opensuse")]
    public void Main_systems_have_a_download_page(string key) =>
        Assert.NotNull(GuestOsCatalog.Find(key)?.DownloadPage);
}
