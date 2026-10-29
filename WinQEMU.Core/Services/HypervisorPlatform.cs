using System.Runtime.InteropServices;

namespace WinQEMU.Core.Services
{
    /// <summary>Where Windows Hypervisor Platform (WHPX) stands on this PC, as far as running accelerated machines goes.</summary>
    public enum HypervisorPlatformState
    {
        /// <summary>The hypervisor runs and accepts virtual machines: QEMU can use WHPX.</summary>
        Ready,

        /// <summary>The Windows feature is not enabled (its library is absent). It can be, as administrator.</summary>
        NotEnabled,

        /// <summary>
        /// The feature is there but no hypervisor runs: Windows has to restart after enabling it, or the hypervisor is kept
        /// from starting (bcdedit hypervisorlaunchtype off).
        /// </summary>
        RestartNeeded,

        /// <summary>No hypervisor runs and the processor's virtualization is off in the firmware (BIOS/UEFI settings).</summary>
        FirmwareVirtualizationOff
    }

    /// <summary>
    /// Asks Windows itself whether WHPX can run a machine, as QEMU does when it starts one (WHvGetCapability), without
    /// administrator rights. The QEMU probe (QemuLocator) says whether the build has WHPX; both are needed, or a machine on
    /// "Automatic" asked for WHPX on a PC without it and did not start at all, where emulation would have run it.
    /// </summary>
    public static class HypervisorPlatform
    {
        public static HypervisorPlatformState Probe()
        {
            if (!NativeLibrary.TryLoad("WinHvPlatform.dll", typeof(HypervisorPlatform).Assembly, DllImportSearchPath.System32, out var library))
                return HypervisorPlatformState.NotEnabled;
            try
            {
                return Classify(true, HypervisorPresent(library), FirmwareVirtualizationEnabled());
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }

        /// <summary>The verdict from what was read, apart so that it can be tested.</summary>
        /// <remarks>
        /// The firmware's virtualization is only believed when the feature is there and no hypervisor runs: once one does
        /// (Hyper-V, say, without this feature), the processor feature reads as off to Windows, the hypervisor having taken
        /// it. So a missing feature is said first: it is also the one thing WinQEMU can fix.
        /// </remarks>
        public static HypervisorPlatformState Classify(bool libraryPresent, bool hypervisorPresent, bool firmwareVirtualization) =>
            !libraryPresent ? HypervisorPlatformState.NotEnabled
            : hypervisorPresent ? HypervisorPlatformState.Ready
            : !firmwareVirtualization ? HypervisorPlatformState.FirmwareVirtualizationOff
            : HypervisorPlatformState.RestartNeeded;

        /// <summary>The command that enables the feature; it needs administrator rights and a restart.</summary>
        public static (string FileName, string Arguments) EnableCommand =>
            (Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart");

        /// <summary>dism's exit codes for "done" and "done, restart required".</summary>
        public static bool EnableSucceeded(int exitCode) => exitCode is 0 or 3010;

        private delegate int GetCapability(int code, out int value, int size, out int written);

        private static bool HypervisorPresent(IntPtr library)
        {
            if (!NativeLibrary.TryGetExport(library, "WHvGetCapability", out var export))
                return false;
            var getCapability = Marshal.GetDelegateForFunctionPointer<GetCapability>(export);
            // WHvCapabilityCodeHypervisorPresent = 0, a BOOL.
            return getCapability(0, out var present, sizeof(int), out _) >= 0 && present != 0;
        }

        // PF_VIRT_FIRMWARE_ENABLED
        private static bool FirmwareVirtualizationEnabled() => IsProcessorFeaturePresent(21);

        [DllImport("kernel32.dll")]
        private static extern bool IsProcessorFeaturePresent(int feature);
    }
}
