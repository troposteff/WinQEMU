using System;
using WinQEMU.Core.Services;

namespace WinQEMU.ViewModels
{
    /// <summary>The "Copy diagnostics" report (Core.Services.DiagnosticReport) for the selected machine, or the PC alone.</summary>
    public partial class MainViewModel
    {
        /// <summary>Package family of the GitHub installation, the one signed CN=troposteff; any other is the Store's.</summary>
        private const string GitHubPublisherId = "_6swaqgnpzyjaw";

        public string BuildDiagnosticReport()
        {
            var package = Windows.ApplicationModel.Package.Current.Id;
            var version = package.Version;
            var channel = package.FamilyName.EndsWith(GitHubPublisherId, StringComparison.OrdinalIgnoreCase)
                ? "GitHub" : "Microsoft Store";

            return DiagnosticReport.Build(
                Core.AppVersion.Display(new Version(version.Major, version.Minor, version.Build, version.Revision)),
                channel,
                HostDescription.Probe(),
                Manager.Installation,
                // Read again rather than _platform: the report should say how things stand now, not at start.
                HypervisorPlatform.Probe(),
                SelectedVm,
                SelectedSession?.State,
                SelectedSession?.Log ?? (System.Collections.Generic.IReadOnlyList<string>)Array.Empty<string>());
        }
    }
}
