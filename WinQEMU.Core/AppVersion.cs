using System.Globalization;

namespace WinQEMU.Core
{
    /// <summary>
    /// The version as people read it (choice of the user): 2026.0.1_beta, 2026.0.2_beta… while features are being
    /// developed, then 2026.1.0 for the release. The package carries the same numbers with a fourth one at 0
    /// (2026.0.1.0): MSIX allows nothing else, no "_beta". A minor version of 0 is what makes a beta, here and in
    /// tools/Build-Package.ps1, which names the release and its tag after it.
    /// </summary>
    public static class AppVersion
    {
        public static bool IsBeta(Version package) => package.Minor == 0;

        /// <summary>"2026.0.1_beta" for package 2026.0.1.0, "2026.1.0" for 2026.1.0.0.</summary>
        public static string Display(Version package) =>
            string.Create(CultureInfo.InvariantCulture, $"{package.Major}.{package.Minor}.{package.Build}")
            + (IsBeta(package) ? "_beta" : string.Empty);
    }
}
