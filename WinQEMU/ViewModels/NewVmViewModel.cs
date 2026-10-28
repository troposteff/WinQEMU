using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// State and validation for the VM creation wizard. Each step reports why it is not satisfied,
    /// so the user is never left with a disabled button and no explanation.
    /// </summary>
    public partial class NewVmViewModel : ObservableObject
    {
        public const int StepCount = 6;

        private readonly VmManager _manager;
        private readonly HashSet<string> _existingNames;

        public NewVmViewModel(VmManager manager)
        {
            _manager = manager;
            _existingNames = new HashSet<string>(
                manager.VirtualMachines.Select(vm => vm.Name),
                StringComparer.CurrentCultureIgnoreCase);

            foreach (var profile in GuestOsCatalog.Profiles)
                Profiles.Add(profile);

            _selectedProfile = GuestOsCatalog.Default;
            ApplyProfile(_selectedProfile);

            Name = MakeUniqueName(DialogText.NewVmDefaultName);

            NetworkChoices = new[] { NetworkChoice.Nat() }
                .Concat(manager.Settings.VirtualNetworks.Select(NetworkChoice.For))
                .Append(NetworkChoice.None())
                .ToList();
        }

        // ---- Navigation ----

        private int _stepIndex;
        public int StepIndex
        {
            get => _stepIndex;
            private set
            {
                if (!SetProperty(ref _stepIndex, value))
                    return;

                RaisePropertyChanged(nameof(StepTitle));
                RaisePropertyChanged(nameof(StepCaption));
                RaisePropertyChanged(nameof(IsLastStep));
                RaisePropertyChanged(nameof(PrimaryButtonText));
                RaisePropertyChanged(nameof(CanGoBack));
                RaiseStepVisibility();
                RevalidateCurrentStep();
            }
        }

        public bool IsStep1 => StepIndex == 0;
        public bool IsStep2 => StepIndex == 1;
        public bool IsStep3 => StepIndex == 2;
        public bool IsStep4 => StepIndex == 3;
        public bool IsStep5 => StepIndex == 4;
        public bool IsStep6 => StepIndex == 5;

        private void RaiseStepVisibility()
        {
            RaisePropertyChanged(nameof(IsStep1));
            RaisePropertyChanged(nameof(IsStep2));
            RaisePropertyChanged(nameof(IsStep3));
            RaisePropertyChanged(nameof(IsStep4));
            RaisePropertyChanged(nameof(IsStep5));
            RaisePropertyChanged(nameof(IsStep6));
        }

        public string StepTitle => StepIndex switch
        {
            0 => DialogText.NewVmStepName,
            1 => DialogText.NewVmStepOs,
            2 => DialogText.NewVmStepPlatform,
            3 => DialogText.NewVmStepDisk,
            4 => DialogText.NewVmStepMedia,
            _ => DialogText.NewVmStepSummary
        };

        public string StepCaption =>
            string.Format(CultureInfo.CurrentCulture, DialogText.NewVmStepCaption, StepIndex + 1, StepCount);

        public bool IsLastStep => StepIndex == StepCount - 1;

        public string PrimaryButtonText => IsLastStep ? DialogText.CreateButton : DialogText.NewVmNext;

        public bool CanGoBack => StepIndex > 0;

        public void GoBack()
        {
            if (CanGoBack)
                StepIndex--;
        }

        /// <summary>Advances a step. Returns false when the current step is not valid.</summary>
        public bool GoNext()
        {
            if (!IsCurrentStepValid || IsLastStep)
                return false;

            StepIndex++;
            return true;
        }

        // ---- Validation ----

        private bool _isCurrentStepValid = true;
        public bool IsCurrentStepValid
        {
            get => _isCurrentStepValid;
            private set => SetProperty(ref _isCurrentStepValid, value);
        }

        private string _validationMessage = string.Empty;
        public string ValidationMessage
        {
            get => _validationMessage;
            private set
            {
                if (SetProperty(ref _validationMessage, value))
                    RaisePropertyChanged(nameof(HasValidationMessage));
            }
        }

        public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

        /// <summary>Non-blocking advisories: the step is valid, but the user should know something.</summary>
        private string _advisory = string.Empty;
        public string Advisory
        {
            get => _advisory;
            private set
            {
                if (SetProperty(ref _advisory, value))
                    RaisePropertyChanged(nameof(HasAdvisory));
            }
        }

        public bool HasAdvisory => !string.IsNullOrEmpty(Advisory);

        private void RevalidateCurrentStep()
        {
            var (error, advisory) = StepIndex switch
            {
                0 => ValidateIdentity(),
                1 => (null, SelectedProfile.Caveats),
                2 => ValidatePlatform(),
                3 => ValidateDisk(),
                4 => ValidateMedia(),
                _ => (null, (string?)null)
            };

            ValidationMessage = error ?? string.Empty;
            Advisory = advisory ?? string.Empty;
            IsCurrentStepValid = error is null;

            if (IsLastStep)
                RaisePropertyChanged(nameof(Summary));
        }

        private (string? Error, string? Advisory) ValidateIdentity()
        {
            if (VmLibrary.ValidateName(Name) is { } problem)
                return (problem, null);

            if (_existingNames.Contains(Name.Trim()))
                return (string.Format(CultureInfo.CurrentCulture, DialogText.NewVmNameExists, Name.Trim()), null);

            if (!_manager.IsQemuAvailable)
                return (DialogText.NewVmNoQemu, null);

            if (Directory.Exists(TargetFolder))
            {
                return (null, string.Format(CultureInfo.CurrentCulture, DialogText.NewVmFolderExists, TargetFolder));
            }

            return (null, null);
        }

        private (string? Error, string? Advisory) ValidatePlatform()
        {
            if (CpuCount < 1)
                return (DialogText.NewVmNeedCpu, null);

            if (MemoryMb < 128)
                return (DialogText.NewVmNeedMemory, null);

            var installation = _manager.Installation;
            if (installation is not null
                && Firmware != FirmwareType.Bios
                && !installation.SupportsUefi(SelectedProfile.Architecture, Firmware == FirmwareType.UefiSecureBoot))
            {
                return (string.Format(CultureInfo.CurrentCulture, DialogText.NewVmNoUefi, installation.InstallDirectory), null);
            }

            if (installation is not null
                && Firmware == FirmwareType.UefiSecureBoot
                && QemuArgumentBuilder.ResolveAccelerator(BuildPreviewConfig(), installation) == AcceleratorType.Whpx)
            {
                return (DialogText.NewVmSecureBootWhpx, null);
            }

            var advisories = new List<string>();

            var hostMemory = HostCapabilities.TotalPhysicalMemoryMb;
            if (hostMemory > 0 && MemoryMb > HostCapabilities.RecommendedMaxGuestMemoryMb)
            {
                advisories.Add(
                    string.Format(CultureInfo.CurrentCulture, DialogText.NewVmMemoryAdvisory, FormatMb(hostMemory), FormatMb(MemoryMb)));
            }

            if (CpuCount > HostCapabilities.LogicalProcessors)
            {
                advisories.Add(
                    string.Format(CultureInfo.CurrentCulture, DialogText.NewVmCpuAdvisory, HostCapabilities.LogicalProcessors));
            }

            if (installation is not null && !installation.SupportsWhpx)
            {
                advisories.Add(DialogText.NewVmNoWhpx);
            }

            return (null, advisories.Count > 0 ? string.Join(Environment.NewLine, advisories) : null);
        }

        private (string? Error, string? Advisory) ValidateDisk()
        {
            if (ValidateEncryption() is { } encryptionProblem)
                return (encryptionProblem, null);

            if (!CreateNewDisk)
            {
                if (string.IsNullOrWhiteSpace(ExistingDiskPath))
                    return (DialogText.NewVmNoDiskFile, null);
                if (!File.Exists(ExistingDiskPath))
                    return (string.Format(CultureInfo.CurrentCulture, DialogText.FileNotFound, ExistingDiskPath), null);

                return (null, DialogText.NewVmExistingDiskAdvisory);
            }

            if (DiskSizeGb < 1)
                return (DialogText.NewVmDiskTooSmall, null);

            if (DiskSizeGb > 8192)
                return (DialogText.NewVmDiskTooLarge, null);

            var free = HostCapabilities.GetFreeSpaceBytes(LibraryRoot);
            if (free >= 0 && DiskCapacityBytes > free)
            {
                return DiskFormat == DiskFormat.Qcow2
                    ? (null, string.Format(CultureInfo.CurrentCulture, DialogText.NewVmSparseAdvisory, Sizes.Format(DiskCapacityBytes), FormatBytes(free)))
                    : (string.Format(CultureInfo.CurrentCulture, DialogText.NewVmNoSpace, DiskFormat, FormatBytes(free)), null);
            }

            return (null, null);
        }

        private (string? Error, string? Advisory) ValidateMedia()
        {
            if (!string.IsNullOrWhiteSpace(IsoPath) && !File.Exists(IsoPath))
                return (string.Format(CultureInfo.CurrentCulture, DialogText.NewVmIsoNotFound, IsoPath), null);

            if (UseUnattended && (IsLinuxUnattended
                    ? UbuntuUnattendedSetup.WhyUserNameIsRefused(UnattendedUser)
                    : WindowsUnattendedSetup.WhyUserNameIsRefused(UnattendedUser)) is { } refused)
                return (refused, null);
            if (UseUnattended && IsLinuxUnattended && UnattendedPassword.Length == 0)
                return (DialogText.NewVmUbuntuPassword, null);

            if (!string.IsNullOrWhiteSpace(SecondaryIsoPath) && !File.Exists(SecondaryIsoPath))
                return (string.Format(CultureInfo.CurrentCulture, DialogText.NewVmSecondIsoNotFound, SecondaryIsoPath), null);

            var advisories = new List<string>();

            if (string.IsNullOrWhiteSpace(IsoPath))
            {
                advisories.Add(DialogText.NewVmNoIsoAdvisory);
            }

            if (UseUnattended && !IsLinuxUnattended && string.IsNullOrWhiteSpace(SecondaryIsoPath))
            {
                advisories.Add(DialogText.NewVmNoVirtioUnattended);
            }
            else if (SelectedProfile.SuggestsVirtioDrivers && string.IsNullOrWhiteSpace(SecondaryIsoPath))
            {
                advisories.Add(DialogText.NewVmVirtioSuggested);
            }

            return (null, advisories.Count > 0 ? string.Join(Environment.NewLine, advisories) : null);
        }

        // ---- Step 1: identity ----

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    RaisePropertyChanged(nameof(TargetFolder));
                    RevalidateCurrentStep();
                }
            }
        }

        private string _description = string.Empty;
        public string Description { get => _description; set => SetProperty(ref _description, value); }

        public string LibraryRoot => _manager.Settings.EffectiveLibraryRoot;

        public string TargetFolder
        {
            get
            {
                var trimmed = (Name ?? string.Empty).Trim();
                if (trimmed.Length == 0 || VmLibrary.ValidateName(trimmed) is not null)
                    return LibraryRoot;
                return Path.Combine(LibraryRoot, trimmed);
            }
        }

        /// <summary>Changes where the library lives. Persisted immediately so it also applies to the shell.</summary>
        public void ChangeLibraryRoot(string path)
        {
            _manager.SetLibraryRoot(path);
            RaisePropertyChanged(nameof(LibraryRoot));
            RaisePropertyChanged(nameof(TargetFolder));
            RevalidateCurrentStep();
        }

        // ---- Step 2: guest OS ----

        public ObservableCollection<GuestOsProfile> Profiles { get; } = new();

        private GuestOsProfile _selectedProfile;
        public GuestOsProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (value is null || !SetProperty(ref _selectedProfile, value))
                    return;

                ApplyProfile(value);
                RaiseUnattended();
                RevalidateCurrentStep();
            }
        }

        private void ApplyProfile(GuestOsProfile profile)
        {
            // The wizard's whole point: one choice sets everything downstream to something that works.
            CpuCount = Math.Min(profile.CpuCount, Math.Max(1, HostCapabilities.LogicalProcessors));
            MemoryMb = Math.Min(profile.MemoryMb, HostCapabilities.RecommendedMaxGuestMemoryMb);
            Firmware = profile.Firmware;
            DiskSizeGb = (int)Math.Round(profile.DiskCapacityGb);
            DiskBus = profile.DiskBus;
            NicModel = profile.NicModel;
            RtcLocalTime = profile.RtcLocalTime;
        }

        // ---- Step 3: platform ----

        private int _cpuCount = 2;
        public int CpuCount
        {
            get => _cpuCount;
            set { if (SetProperty(ref _cpuCount, value)) RevalidateCurrentStep(); }
        }

        private int _memoryMb = 4096;
        public int MemoryMb
        {
            get => _memoryMb;
            set { if (SetProperty(ref _memoryMb, value)) RevalidateCurrentStep(); }
        }

        private FirmwareType _firmware = FirmwareType.Uefi;
        public FirmwareType Firmware
        {
            get => _firmware;
            set { if (SetProperty(ref _firmware, value)) RevalidateCurrentStep(); }
        }

        public IReadOnlyList<FirmwareType> FirmwareOptions { get; } = new[]
        {
            FirmwareType.Bios, FirmwareType.Uefi, FirmwareType.UefiSecureBoot
        };

        private bool _rtcLocalTime;
        public bool RtcLocalTime { get => _rtcLocalTime; set => SetProperty(ref _rtcLocalTime, value); }

        public string HostSummary =>
            string.Format(CultureInfo.CurrentCulture, DialogText.NewVmHostSummary, HostCapabilities.LogicalProcessors, FormatMb(HostCapabilities.TotalPhysicalMemoryMb));

        // ---- Step 4: disk ----

        private bool _createNewDisk = true;
        public bool CreateNewDisk
        {
            get => _createNewDisk;
            set
            {
                if (!SetProperty(ref _createNewDisk, value))
                    return;
                RaisePropertyChanged(nameof(UseExistingDisk));
                RevalidateCurrentStep();
            }
        }

        public bool UseExistingDisk
        {
            get => !_createNewDisk;
            set => CreateNewDisk = !value;
        }

        private int _diskSizeGb = 32;
        public int DiskSizeGb
        {
            get => _diskSizeGb;
            set { if (SetProperty(ref _diskSizeGb, value)) RevalidateCurrentStep(); }
        }

        public long DiskCapacityBytes => (long)DiskSizeGb * 1024 * 1024 * 1024;

        private DiskFormat _diskFormat = DiskFormat.Qcow2;
        public DiskFormat DiskFormat
        {
            get => _diskFormat;
            set { if (SetProperty(ref _diskFormat, value)) RevalidateCurrentStep(); }
        }

        public IReadOnlyList<DiskFormat> DiskFormatOptions { get; } = new[]
        {
            DiskFormat.Qcow2, DiskFormat.Raw, DiskFormat.Vhdx, DiskFormat.Vmdk
        };

        private DiskBus _diskBus = DiskBus.VirtioBlk;
        public DiskBus DiskBus { get => _diskBus; set => SetProperty(ref _diskBus, value); }

        public IReadOnlyList<DiskBus> DiskBusOptions { get; } = new[]
        {
            DiskBus.VirtioBlk, DiskBus.Sata, DiskBus.Nvme, DiskBus.Ide
        };

        private string _existingDiskPath = string.Empty;
        public string ExistingDiskPath
        {
            get => _existingDiskPath;
            set { if (SetProperty(ref _existingDiskPath, value)) RevalidateCurrentStep(); }
        }

        // ---- Step 5: media and network ----

        private string _isoPath = string.Empty;
        public string IsoPath
        {
            get => _isoPath;
            set { if (SetProperty(ref _isoPath, value)) RaiseUnattended(); }
        }

        private string _secondaryIsoPath = string.Empty;
        public string SecondaryIsoPath
        {
            get => _secondaryIsoPath;
            set { if (SetProperty(ref _secondaryIsoPath, value)) RevalidateCurrentStep(); }
        }

        private bool _bootFromIso = true;
        public bool BootFromIso
        {
            get => _bootFromIso;
            set { if (SetProperty(ref _bootFromIso, value)) RaiseUnattended(); }
        }

        // ---- Unattended installation (Services.WindowsUnattendedSetup) ----

        /// <summary>
        /// Offered for a Windows from 10 on, in UEFI, starting from its ISO: VMware's "installation
        /// simplifiée". Windows 7 and 8.1 keep BIOS and their own answer file format.
        /// </summary>
        public bool CanInstallUnattended =>
            BootFromIso && !string.IsNullOrWhiteSpace(IsoPath)
            && (SelectedProfile.Family == "Windows"
                ? SelectedProfile.Key != "windows-7" && Firmware == FirmwareType.Uefi
                : SelectedProfile.Key == "ubuntu" && IsUbuntuInstaller(IsoPath));

        /// <summary>Ubuntu's own unattended installation (UbuntuUnattendedSetup) rather than Windows'.</summary>
        public bool IsLinuxUnattended => SelectedProfile.Family == "Linux";

        public string UnattendedHint => IsLinuxUnattended
            ? DialogText.NewVmUnattendedUbuntu
            : DialogText.NewVmUnattendedWindows;

        // Reading the ISO's directory on every keystroke of a path would be wasteful: once per file.
        private readonly Dictionary<string, bool> _ubuntuInstallers = new(StringComparer.OrdinalIgnoreCase);

        private bool IsUbuntuInstaller(string isoPath)
        {
            if (!File.Exists(isoPath))
                return false;
            if (!_ubuntuInstallers.TryGetValue(isoPath, out var found))
                _ubuntuInstallers[isoPath] = found = UbuntuUnattendedSetup.CanInstall(isoPath);
            return found;
        }

        private bool _unattended = true;
        public bool Unattended
        {
            get => _unattended;
            set { if (SetProperty(ref _unattended, value)) RaiseUnattended(); }
        }

        public bool UseUnattended => CanInstallUnattended && Unattended;

        private string _unattendedUser = Environment.UserName;
        public string UnattendedUser
        {
            get => _unattendedUser;
            set { if (SetProperty(ref _unattendedUser, value)) RevalidateCurrentStep(); }
        }

        /// <summary>Kept in memory only: it goes into the answer file, never into the descriptor.</summary>
        public string UnattendedPassword { get; set; } = string.Empty;

        public string UnattendedComputerName => IsLinuxUnattended
            ? UbuntuUnattendedSetup.HostnameFor(Name)
            : WindowsUnattendedSetup.ComputerNameFor(Name);

        private void RaiseUnattended()
        {
            // Linux account names are lower case: "Stéphane" from Windows becomes "stephane".
            if (IsLinuxUnattended && UbuntuUnattendedSetup.WhyUserNameIsRefused(UnattendedUser) is not null)
            {
                var folded = UbuntuUnattendedSetup.HostnameFor(UnattendedUser).Replace("-", string.Empty, StringComparison.Ordinal);
                if (UbuntuUnattendedSetup.WhyUserNameIsRefused(folded) is null)
                    _unattendedUser = folded;
                RaisePropertyChanged(nameof(UnattendedUser));
            }
            RaisePropertyChanged(nameof(IsLinuxUnattended));
            RaisePropertyChanged(nameof(UnattendedHint));
            RaisePropertyChanged(nameof(UnattendedComputerName));
            RaisePropertyChanged(nameof(CanInstallUnattended));
            RaisePropertyChanged(nameof(UseUnattended));
            RevalidateCurrentStep();
        }

        private string _networkConnection = NetworkAdapterConfig.NatConnection;
        /// <summary>What the machine's card is plugged into, as <see cref="NetworkAdapterConfig.Connection"/> takes it.</summary>
        public string NetworkConnection
        {
            get => _networkConnection;
            set { if (value is not null && SetProperty(ref _networkConnection, value)) RevalidateCurrentStep(); }
        }

        /// <summary>NAT, the virtual networks, or no card at all. The dialog is modal: the networks cannot change meanwhile.</summary>
        public IReadOnlyList<NetworkChoice> NetworkChoices { get; }

        private NetworkAdapterConfig ChosenConnection()
        {
            var adapter = new NetworkAdapterConfig();
            adapter.Connection = NetworkConnection;
            return adapter;
        }

        private NetworkMode NetworkMode => ChosenConnection().Mode;

        private NicModel _nicModel = NicModel.VirtioNet;
        public NicModel NicModel { get => _nicModel; set => SetProperty(ref _nicModel, value); }

        // ---- Step 6: summary ----

        public string Summary
        {
            get
            {
                var builder = new StringBuilder();
                var culture = CultureInfo.CurrentCulture;
                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryName, Name.Trim()));
                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryLocation, TargetFolder));
                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryGuest, SelectedProfile.DisplayName, SelectedProfile.Architecture));
                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryFirmware, DescribeFirmware(Firmware)));
                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryCpus, CpuCount));
                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryMemory, FormatMb(MemoryMb)));

                builder.AppendLine(CreateNewDisk
                    ? string.Format(culture, DialogText.NewVmSummaryNewDisk, Sizes.Format(DiskCapacityBytes),
                        DiskFormat.ToString().ToLowerInvariant(), DescribeBus(DiskBus))
                    : string.Format(culture, DialogText.NewVmSummaryExistingDisk, ExistingDiskPath, DescribeBus(DiskBus)));
                if (UseEncryption)
                    builder.AppendLine(RememberDiskPassword ? EncryptionText.WizardSummaryRemembered : EncryptionText.WizardSummaryAsked);

                builder.AppendLine(string.IsNullOrWhiteSpace(IsoPath)
                    ? DialogText.NewVmSummaryNoMedia
                    : string.Format(culture, BootFromIso ? DialogText.NewVmSummaryMediaBoot : DialogText.NewVmSummaryMedia, IsoPath));

                if (!string.IsNullOrWhiteSpace(SecondaryIsoPath))
                    builder.AppendLine(string.Format(culture, DialogText.NewVmSummarySecondDrive, SecondaryIsoPath));

                builder.AppendLine(string.Format(culture, DialogText.NewVmSummaryNetwork, NetworkChoices.FirstOrDefault(c => c.Key == NetworkConnection)?.Label ?? DescribeNetwork(NetworkMode)));
                builder.Append(RtcLocalTime ? DialogText.NewVmSummaryClockLocal : DialogText.NewVmSummaryClockUtc);

                return builder.ToString();
            }
        }

        /// <summary>
        /// The exact QEMU command line this configuration will produce. Shown so the user can see what
        /// the wizard is actually going to run rather than taking it on trust.
        /// </summary>
        public string CommandPreview
        {
            get
            {
                var installation = _manager.Installation;
                if (installation is null)
                    return DialogText.NewVmPreviewNoQemu;

                try
                {
                    var config = BuildPreviewConfig();
                    var executable = installation.GetSystemBinary(config.Architecture);
                    // A representative port; the real one is allocated at launch.
                    var arguments = QemuArgumentBuilder.Build(config, installation, 45000, networkPorts: _ => 45001);
                    return QemuArgumentBuilder.ToDisplayString(executable, arguments);
                }
                catch (Exception ex)
                {
                    return string.Format(CultureInfo.CurrentCulture, DialogText.NewVmPreviewFailed, ex.Message);
                }
            }
        }

        private VmConfig BuildPreviewConfig()
        {
            var config = new VmConfig
            {
                Name = Name.Trim(),
                Architecture = SelectedProfile.Architecture,
                Firmware = Firmware,
                CpuCount = CpuCount,
                MemoryMb = MemoryMb,
                IsoPath = IsoPath,
                SecondaryIsoPath = SecondaryIsoPath,
                BootFromIso = BootFromIso && !string.IsNullOrWhiteSpace(IsoPath),
                RtcLocalTime = RtcLocalTime,
                FolderPath = TargetFolder
            };

            config.Disks.Add(new DiskConfig
            {
                Id = "disk0",
                FileName = CreateNewDisk ? "disk0." + DiskExtension(DiskFormat) : ExistingDiskPath,
                Format = DiskFormat,
                Bus = DiskBus,
                CapacityBytes = DiskCapacityBytes,
                BootIndex = 0
            });

            if (NetworkMode != NetworkMode.None)
            {
                config.NetworkAdapters.Add(new NetworkAdapterConfig
                {
                    Id = "net0",
                    Mode = NetworkMode,
                    NetworkId = ChosenConnection().NetworkId,
                    Model = NicModel,
                    MacAddress = "52:54:00:xx:xx:xx"
                });
            }

            return config;
        }

        public CreateVmRequest BuildRequest() => new()
        {
            Name = Name.Trim(),
            Description = Description,
            GuestOs = SelectedProfile.Key,
            Architecture = SelectedProfile.Architecture,
            Firmware = Firmware,
            CpuCount = CpuCount,
            MemoryMb = MemoryMb,
            DiskCapacityBytes = CreateNewDisk ? DiskCapacityBytes : 0,
            DiskFormat = DiskFormat,
            DiskBus = DiskBus,
            ExistingDiskPath = CreateNewDisk ? string.Empty : ExistingDiskPath,
            IsoPath = IsoPath,
            SecondaryIsoPath = SecondaryIsoPath,
            BootFromIso = BootFromIso,
            NetworkMode = NetworkMode,
            NetworkId = ChosenConnection().NetworkId,
            NicModel = NicModel,
            RtcLocalTime = RtcLocalTime,
            Unattended = UseUnattended && !IsLinuxUnattended
                ? WindowsUnattendedSetup.ForHost(UnattendedUser, UnattendedPassword, Name.Trim(), IsoPath, SelectedProfile.Key)
                : null,
            UnattendedLinux = UseUnattended && IsLinuxUnattended
                ? UbuntuUnattendedSetup.ForHost(UnattendedUser, UnattendedPassword, Name.Trim())
                : null,
            DiskKey = UseEncryption ? new DiskKey(DiskPassword) : null,
            RememberDiskPassword = UseEncryption && RememberDiskPassword
        };

        // ---- Display helpers ----

        public static string DescribeFirmware(FirmwareType firmware) => firmware switch
        {
            FirmwareType.Bios => DialogText.FirmwareBios,
            FirmwareType.Uefi => "UEFI",
            FirmwareType.UefiSecureBoot => DialogText.FirmwareUefiSecureBoot,
            _ => firmware.ToString()
        };

        public static string DescribeBus(DiskBus bus) => bus switch
        {
            DiskBus.VirtioBlk => DialogText.BusVirtio,
            DiskBus.Sata => DialogText.BusSata,
            DiskBus.Nvme => "NVMe",
            DiskBus.Ide => DialogText.BusIde,
            DiskBus.LsiSas => DialogText.BusLsiSas,
            DiskBus.Pvscsi => DialogText.BusPvscsi,
            _ => bus.ToString()
        };

        public static string DescribeNetwork(NetworkMode mode) => mode switch
        {
            NetworkMode.Nat => DialogText.NetworkModeNat,
            NetworkMode.Bridged => DialogText.NetworkModeBridged,
            NetworkMode.Internal => DialogText.NetworkModeMulticast,
            NetworkMode.Network => DialogText.NetworkModeVirtual,
            NetworkMode.None => DialogText.NetworkModeNone,
            _ => mode.ToString()
        };

        private static string DiskExtension(DiskFormat format) => format switch
        {
            DiskFormat.Qcow2 => "qcow2",
            DiskFormat.Raw => "img",
            DiskFormat.Vhdx => "vhdx",
            DiskFormat.Vmdk => "vmdk",
            DiskFormat.Vdi => "vdi",
            _ => "qcow2"
        };

        private static string FormatMb(int megabytes) => Sizes.Format(megabytes * 1024d * 1024);

        private static string FormatBytes(long bytes) => Sizes.Format(bytes);

        private string MakeUniqueName(string baseName)
        {
            if (!_existingNames.Contains(baseName))
                return baseName;

            for (var index = 2; ; index++)
            {
                var candidate = $"{baseName} {index.ToString(CultureInfo.CurrentCulture)}";
                if (!_existingNames.Contains(candidate))
                    return candidate;
            }
        }
    }
}
