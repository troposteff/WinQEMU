using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinQEMU.Core.Models;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// Finds a usable QEMU installation. QEMU has no single canonical location on Windows, so we
    /// probe the installer's registry key, PATH, and the layouts used by the common package
    /// managers, then validate each candidate rather than trusting the path.
    /// </summary>
    public static class QemuLocator
    {
        public static string SystemBinaryName(GuestArchitecture architecture) => architecture switch
        {
            GuestArchitecture.X86_64 => "qemu-system-x86_64.exe",
            GuestArchitecture.Aarch64 => "qemu-system-aarch64.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture))
        };

        /// <summary>
        /// Probes for QEMU. <paramref name="configuredDirectory"/> comes from application settings
        /// and, when set, is the only candidate considered so an explicit choice is never overridden.
        /// </summary>
        public static async Task<QemuDetectionResult> DetectAsync(
            string? configuredDirectory = null,
            CancellationToken cancellationToken = default)
        {
            var rejected = new List<RejectedQemuCandidate>();

            var candidates = string.IsNullOrWhiteSpace(configuredDirectory)
                ? EnumerateCandidateDirectories()
                : new[] { configuredDirectory };

            foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory))
                    continue;

                var binaries = new Dictionary<GuestArchitecture, string>();
                foreach (var architecture in Enum.GetValues<GuestArchitecture>())
                {
                    var path = Path.Combine(directory, SystemBinaryName(architecture));
                    if (File.Exists(path))
                        binaries[architecture] = path;
                }

                if (binaries.Count == 0)
                {
                    // PATH holds dozens of directories that were never going to be QEMU. Only
                    // report a rejection when the folder actually looks like a QEMU install,
                    // otherwise the diagnostics drown out the one entry that matters.
                    if (LooksLikeQemu(directory))
                    {
                        rejected.Add(new RejectedQemuCandidate(directory,
                            PlatformText.LocatorHelperToolsOnly));
                    }
                    continue;
                }

                var qemuImg = Path.Combine(directory, "qemu-img.exe");
                if (!File.Exists(qemuImg))
                {
                    rejected.Add(new RejectedQemuCandidate(directory,
                        PlatformText.LocatorQemuImgMissing));
                    continue;
                }

                var primary = binaries.TryGetValue(GuestArchitecture.X86_64, out var x64)
                    ? x64
                    : binaries.Values.First();

                return new QemuDetectionResult
                {
                    Installation = new QemuInstallation
                    {
                        InstallDirectory = directory,
                        SystemBinaries = binaries,
                        QemuImgPath = qemuImg,
                        Version = await ProbeVersionAsync(primary, cancellationToken).ConfigureAwait(false),
                        WhpxInBuild = await ProbeWhpxAsync(primary, cancellationToken).ConfigureAwait(false),
                        Platform = HypervisorPlatform.Probe(),
                        UefiCodeX64 = FindFirmware(directory, "edk2-x86_64-code.fd", "OVMF_CODE.fd", "OVMF_CODE_4M.fd"),
                        UefiCodeX64SecureBoot = FindFirmware(directory, "edk2-x86_64-secure-code.fd", "OVMF_CODE.secboot.fd"),
                        UefiVarsTemplateX64 = FindFirmware(directory, "edk2-i386-vars.fd", "OVMF_VARS.fd", "OVMF_VARS_4M.fd"),
                        UefiCodeAarch64 = FindFirmware(directory, "edk2-aarch64-code.fd", "AAVMF_CODE.fd"),
                        UefiVarsTemplateAarch64 = FindFirmware(directory, "edk2-arm-vars.fd", "AAVMF_VARS.fd")
                    },
                    Rejected = rejected
                };
            }

            return new QemuDetectionResult { Rejected = rejected };
        }

        /// <summary>True when the directory ships any QEMU executable, complete install or not.</summary>
        private static bool LooksLikeQemu(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory, "qemu*.exe").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static IEnumerable<string> EnumerateCandidateDirectories()
        {
            // The weilnetz.de installer — the de facto Windows build — records its location here.
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? installDir = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\QEMU");
                    installDir = key?.GetValue("Install_Dir") as string;
                }
                catch
                {
                    // Registry unavailable or access denied; fall through to the filesystem probes.
                }

                if (!string.IsNullOrWhiteSpace(installDir))
                    yield return installDir;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(programFiles, "qemu");
            yield return Path.Combine(programFilesX86, "qemu");
            yield return Path.Combine(localAppData, "Programs", "qemu");
            yield return @"C:\ProgramData\chocolatey\lib\qemu\tools\qemu";
            yield return Path.Combine(userProfile, "scoop", "apps", "qemu", "current");
            yield return @"C:\msys64\mingw64\bin";
            yield return @"C:\tools\qemu";

            // Anything on PATH wins over nothing, but ranks below the well-known locations.
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = entry.Trim().Trim('"');
                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }

        private static string? FindFirmware(string directory, params string[] fileNames)
        {
            // Windows builds drop firmware in the install root; MSYS2 and Linux-style layouts use share/.
            var searchRoots = new[]
            {
                directory,
                Path.Combine(directory, "share"),
                Path.Combine(directory, "share", "qemu"),
                Path.Combine(directory, "..", "share", "qemu")
            };

            foreach (var root in searchRoots)
            {
                foreach (var fileName in fileNames)
                {
                    var candidate = Path.Combine(root, fileName);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        /// <summary>
        /// Ceiling for a single probe. Detection blocks the caller, so a QEMU binary that never
        /// returns must not be able to stall startup indefinitely.
        /// </summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

        private static async Task<string?> ProbeVersionAsync(string systemBinary, CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CreateProbeTimeout(cancellationToken);
                var result = await ProcessRunner.RunAsync(systemBinary, new[] { "--version" }, timeout.Token)
                    .ConfigureAwait(false);
                var match = Regex.Match(result.StandardOutput, @"version\s+([0-9]+(?:\.[0-9]+)*)");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> ProbeWhpxAsync(string systemBinary, CancellationToken cancellationToken)
        {
            // Asking QEMU what it supports: the build may simply lack WHPX support even when the platform has it
            // enabled. The platform is asked too (HypervisorPlatform), as the build having it says nothing of Windows.
            try
            {
                using var timeout = CreateProbeTimeout(cancellationToken);
                var result = await ProcessRunner.RunAsync(systemBinary, new[] { "-accel", "help" }, timeout.Token)
                    .ConfigureAwait(false);
                return (result.StandardOutput + result.StandardError)
                    .Contains("whpx", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static CancellationTokenSource CreateProbeTimeout(CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(ProbeTimeout);
            return linked;
        }
    }
}
