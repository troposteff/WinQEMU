using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    /// <summary>A validated QEMU installation: the binaries and firmware images we can actually use.</summary>
    public sealed class QemuInstallation
    {
        public required string InstallDirectory { get; init; }

        /// <summary>Emulator binary per guest architecture. Missing entries mean that guest type is unavailable.</summary>
        public required IReadOnlyDictionary<GuestArchitecture, string> SystemBinaries { get; init; }

        public required string QemuImgPath { get; init; }

        /// <summary>Version reported by <c>--version</c>, or null when the probe has not run yet.</summary>
        public string? Version { get; init; }

        /// <summary>True when QEMU lists <c>whpx</c> in <c>-accel help</c>: this build can use hardware acceleration.</summary>
        public bool WhpxInBuild { get; init; }

        /// <summary>Windows Hypervisor Platform on this PC, as it stood at detection (Services.HypervisorPlatform).</summary>
        public Services.HypervisorPlatformState Platform { get; init; } = Services.HypervisorPlatformState.Ready;

        /// <summary>
        /// Hardware acceleration is usable: the build has it and Windows runs the hypervisor. What "Automatic" goes by; with
        /// the build alone, a PC without the feature was given WHPX and its machines did not start.
        /// </summary>
        public bool SupportsWhpx
        {
            get => WhpxInBuild && Platform == Services.HypervisorPlatformState.Ready;
            init => WhpxInBuild = value;
        }

        /// <summary>Read-only UEFI firmware volume, keyed by architecture and secure-boot flag.</summary>
        public string? UefiCodeX64 { get; init; }
        public string? UefiCodeX64SecureBoot { get; init; }
        public string? UefiVarsTemplateX64 { get; init; }
        public string? UefiCodeAarch64 { get; init; }
        public string? UefiVarsTemplateAarch64 { get; init; }

        public bool SupportsArchitecture(GuestArchitecture architecture) =>
            SystemBinaries.ContainsKey(architecture);

        public string GetSystemBinary(GuestArchitecture architecture)
        {
            if (SystemBinaries.TryGetValue(architecture, out var path))
                return path;

            throw new QemuNotAvailableException(
                $"This QEMU installation has no emulator for {architecture}. " +
                $"Expected {QemuLocator.SystemBinaryName(architecture)} in {InstallDirectory}.");
        }

        /// <summary>True when UEFI boot can be offered for the given architecture and secure-boot mode.</summary>
        public bool SupportsUefi(GuestArchitecture architecture, bool secureBoot) => architecture switch
        {
            GuestArchitecture.X86_64 => secureBoot
                ? UefiCodeX64SecureBoot is not null && UefiVarsTemplateX64 is not null
                : UefiCodeX64 is not null && UefiVarsTemplateX64 is not null,
            GuestArchitecture.Aarch64 => !secureBoot
                && UefiCodeAarch64 is not null && UefiVarsTemplateAarch64 is not null,
            _ => false
        };
    }

    /// <summary>A directory that looked like QEMU but could not be used, with the reason why.</summary>
    public sealed record RejectedQemuCandidate(string Directory, string Reason);

    public sealed class QemuDetectionResult
    {
        public QemuInstallation? Installation { get; init; }

        /// <summary>
        /// Directories that were inspected and turned down. Surfacing these matters: a partial
        /// install (for example the guest-agent package, which ships qemu-ga but no emulator)
        /// looks like success to the user but cannot run a VM.
        /// </summary>
        public IReadOnlyList<RejectedQemuCandidate> Rejected { get; init; } = Array.Empty<RejectedQemuCandidate>();

        public bool Found => Installation is not null;
    }

    public sealed class QemuNotAvailableException : Exception
    {
        public QemuNotAvailableException(string message) : base(message) { }
    }
}
