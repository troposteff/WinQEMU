using WinQEMU.Core;

namespace WinQEMU.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("2026.0.1.0", "2026.0.1_beta")]
    [InlineData("2026.0.12.0", "2026.0.12_beta")]
    [InlineData("2026.1.0.0", "2026.1.0")]
    [InlineData("2026.1.3.0", "2026.1.3")]
    public void The_package_version_reads_as_the_user_numbers_it(string package, string shown)
    {
        Assert.Equal(shown, AppVersion.Display(Version.Parse(package)));
    }

    [Fact]
    public void Each_beta_and_then_the_release_rank_higher_than_the_last_one()
    {
        // App Installer only ever updates to a higher package version.
        var order = new[] { "2026.0.1.0", "2026.0.2.0", "2026.0.10.0", "2026.1.0.0" }.Select(Version.Parse).ToList();
        Assert.Equal(order, order.OrderBy(v => v));
    }
}
