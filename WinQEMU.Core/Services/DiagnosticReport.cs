using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Services
{
    /// <summary>What a bug report needs about the PC, gathered once when the report is copied.</summary>
    public sealed record HostDescription(
        string Windows,
        string Processor,
        int LogicalProcessors,
        long MemoryBytes,
        string SmartAppControl)
    {
        /// <summary>
        /// Read from the registry and the runtime, without rights. The processor matters most: the openSUSE freeze
        /// investigated on a Ryzen took several exchanges just to learn which CPU it ran on.
        /// </summary>
        public static HostDescription Probe()
        {
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            // ProductName still says "Windows 10" on Windows 11: the build number is what tells them apart.
            var windows = string.Create(CultureInfo.InvariantCulture,
                $"{nt?.GetValue("ProductName")} {nt?.GetValue("DisplayVersion")}, build {nt?.GetValue("CurrentBuild")}.{nt?.GetValue("UBR")}");

            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var processor = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";

            return new HostDescription(
                windows.Trim(),
                processor,
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                ReadSmartAppControl());
        }

        // Smart App Control blocks self-signed packages and may block QEMU's own binaries (signed with a certificate
        // that expired in 2023): worth knowing before looking for a bug.
        private static string ReadSmartAppControl()
        {
            using var policy = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            return policy?.GetValue("VerifiedAndReputablePolicyState") switch
            {
                0 => "off",
                1 => "on",
                2 => "evaluation",
                _ => "unknown"
            };
        }
    }

    /// <summary>
    /// The text the "Copy diagnostics" button puts on the clipboard, meant to be pasted into a GitHub issue: versions,
    /// PC, QEMU, hardware acceleration, the selected machine and the end of its log (which holds QEMU's command line).
    /// In English whatever the interface language: it is read by whoever fixes the bug, next to QEMU's own messages.
    /// </summary>
    public static class DiagnosticReport
    {
        /// <summary>Lines of the machine's log kept: enough for the command line and what QEMU said after it.</summary>
        public const int LogLines = 300;

        public static string Build(
            string appVersion,
            string channel,
            HostDescription host,
            QemuInstallation? qemu,
            HypervisorPlatformState platform,
            VmConfig? machine,
            VmState? state,
            IReadOnlyList<string> log)
        {
            var text = new StringBuilder();
            void Line(string value) => text.Append(value).Append('\n');

            Line("### WinQEMU diagnostics");
            Line(string.Empty);
            Line($"- WinQEMU: {appVersion} ({channel})");
            Line($"- Windows: {host.Windows}");
            Line($"- Processor: {host.Processor}, {host.LogicalProcessors} logical processors");
            Line(string.Create(CultureInfo.InvariantCulture, $"- Memory: {host.MemoryBytes / (1024.0 * 1024 * 1024):0.0} GiB"));
            Line($"- Windows Hypervisor Platform: {platform}");
            Line($"- Smart App Control: {host.SmartAppControl}");
            if (qemu is null)
            {
                Line("- QEMU: not found");
            }
            else
            {
                Line($"- QEMU: {qemu.Version ?? "unknown version"}, in {qemu.InstallDirectory}");
                Line($"- WHPX in this QEMU build: {(qemu.WhpxInBuild ? "yes" : "no")}");
            }

            if (machine is not null)
            {
                Line(string.Empty);
                Line("### Machine");
                Line(string.Empty);
                // The name first: without it, a report copied with another machine selected than the one meant was read
                // as Debian's when it was pfSense's (both "linux" for a profile that says little).
                Line($"- Name: {machine.Name}");
                Line($"- Guest profile: {(machine.GuestOs.Length == 0 ? "unspecified" : machine.GuestOs)}, {machine.Architecture}, {machine.Firmware}");
                Line($"- State: {state?.ToString() ?? "unknown"}");
                Line($"- Accelerator: {machine.Accelerator}, machine type {machine.MachineType}");
                Line(string.Create(CultureInfo.InvariantCulture,
                    $"- CPU: {(machine.CpuModel.Length == 0 ? VmConfig.DefaultCpuModel : machine.CpuModel)}, {machine.CpuCount} vCPU, {machine.CoresPerSocket} cores per socket"));
                Line(string.Create(CultureInfo.InvariantCulture, $"- Memory: {machine.MemoryMb} MiB"));
                Line(string.Empty);
                Line(log.Count > LogLines ? $"### Log (last {LogLines} lines)" : "### Log");
                Line(string.Empty);
                Line("```");
                foreach (var entry in log.Skip(Math.Max(0, log.Count - LogLines)))
                    Line(entry.Replace("```", "'''", StringComparison.Ordinal));
                Line("```");
            }

            return Redact(text.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.UserName, Environment.MachineName);
        }

        /// <summary>
        /// The report is pasted into a public issue: the user's profile path, account and PC names are replaced (paths
        /// to ISOs and disks run through the profile). Machine names and disk file names are kept: they explain the log.
        /// </summary>
        public static string Redact(string text, string userProfile, string userName, string machineName)
        {
            if (userProfile.Length > 0)
                text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            // Whole words only: a three-letter account name would otherwise eat into ordinary words.
            if (userName.Length > 0)
                text = Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(userName)}(?![\w-])", "<user>", RegexOptions.IgnoreCase);
            if (machineName.Length > 0)
                text = Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(machineName)}(?![\w-])", "<pc>", RegexOptions.IgnoreCase);
            return text;
        }
    }
}
