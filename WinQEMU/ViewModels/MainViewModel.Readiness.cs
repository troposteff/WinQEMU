using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WinQEMU.Core.Services;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// Whether this PC can run machines, and what to do when it cannot (Dialogs.ReadinessDialog): QEMU, which WinQEMU never
    /// installs itself, and Windows Hypervisor Platform, which it enables only on request, as administrator. Opened by itself
    /// at start while something is missing, until the user says not to; always from the settings.
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>The command the assistant offers to copy: WinQEMU runs nothing it did not ship.</summary>
        public const string QemuWingetCommand = "winget install SoftwareFreedomConservancy.QEMU";

        public const string QemuDownloadUrl = "https://qemu.weilnetz.de/w64/";

        private HypervisorPlatformState _platform = HypervisorPlatform.Probe();
        private bool _platformEnabledThisRun;

        /// <summary>Asks Windows again, for the assistant: the feature may have been enabled since detection.</summary>
        public void RefreshReadiness()
        {
            _platform = HypervisorPlatform.Probe();
            RaiseReadiness();
        }

        public bool IsQemuReady => Manager.Installation is not null;

        public bool IsAccelerationReady => _platform == HypervisorPlatformState.Ready && Manager.Installation?.WhpxInBuild != false;

        /// <summary>Ready to run accelerated machines: nothing for the assistant to say.</summary>
        public bool IsPcReady => IsQemuReady && IsAccelerationReady;

        /// <summary>Opened by itself at start while QEMU or the acceleration is missing, unless the user said not to.</summary>
        public bool ShouldOfferReadiness => _hasProbedQemu && !IsPcReady && !Settings.ReadinessDismissed;

        public bool ReadinessDismissed
        {
            get => Settings.ReadinessDismissed;
            set
            {
                if (Settings.ReadinessDismissed == value)
                    return;
                Settings.ReadinessDismissed = value;
                Settings.Save();
                RaisePropertyChanged();
            }
        }

        public string QemuReadinessText => Manager.Installation is { } installation
            ? string.Format(CultureInfo.CurrentCulture, DialogText.ReadinessQemuFound, installation.Version ?? "?", installation.InstallDirectory,
                ArchitecturesDisplay)
            : DialogText.ReadinessQemuMissing;

        public string AccelerationReadinessText =>
            _platformEnabledThisRun && _platform != HypervisorPlatformState.Ready ? DialogText.ReadinessWhpxRestart
            : Manager.Installation is { WhpxInBuild: false } && _platform == HypervisorPlatformState.Ready ? DialogText.ReadinessWhpxNotInBuild
            : _platform switch
            {
                HypervisorPlatformState.Ready => DialogText.ReadinessWhpxReady,
                HypervisorPlatformState.NotEnabled => DialogText.ReadinessWhpxNotEnabled,
                HypervisorPlatformState.RestartNeeded => DialogText.ReadinessWhpxRestartOrBlocked,
                _ => DialogText.ReadinessWhpxFirmware
            };

        /// <summary>Only what WinQEMU can do: enabling the feature. A restart or the firmware's settings are the user's.</summary>
        public bool CanEnableAcceleration => _platform == HypervisorPlatformState.NotEnabled && !_platformEnabledThisRun;

        /// <summary>
        /// Enables Windows Hypervisor Platform with dism, as administrator (one UAC prompt, after the user's confirmation in
        /// the assistant). Returns what to tell the user: a restart is always needed before machines can use it.
        /// </summary>
        public async Task<string> EnableAccelerationAsync()
        {
            var (fileName, arguments) = HypervisorPlatform.EnableCommand;
            int exitCode;
            try
            {
                exitCode = await Elevation.RunAsync(fileName, arguments);
            }
            catch (OperationCanceledException)
            {
                return DialogText.ReadinessWhpxDeclined;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return string.Format(CultureInfo.CurrentCulture, DialogText.ReadinessWhpxFailed, ex.Message);
            }

            AppendDiagnostic($"Windows Hypervisor Platform: dism exited with {exitCode}.");
            if (!HypervisorPlatform.EnableSucceeded(exitCode))
                return string.Format(CultureInfo.CurrentCulture, DialogText.ReadinessWhpxFailed, exitCode);
            _platformEnabledThisRun = true;
            RefreshReadiness();
            return DialogText.ReadinessWhpxRestart;
        }

        private void RaiseReadiness()
        {
            RaisePropertyChanged(nameof(IsQemuReady));
            RaisePropertyChanged(nameof(IsAccelerationReady));
            RaisePropertyChanged(nameof(IsPcReady));
            RaisePropertyChanged(nameof(QemuReadinessText));
            RaisePropertyChanged(nameof(AccelerationReadinessText));
            RaisePropertyChanged(nameof(CanEnableAcceleration));
        }
    }
}
