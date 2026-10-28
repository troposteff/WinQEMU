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

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// State and validation for the VM creation wizard. Each step reports why it is not satisfied,
    /// so the user is never left with a disabled button and no explanation.
    /// </summary>
    public class NewVmViewModel : ObservableObject
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

            Name = MakeUniqueName("Nouvelle machine virtuelle");

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
            0 => "Nom et emplacement",
            1 => "Système d'exploitation invité",
            2 => "Processeur, mémoire et microprogramme",
            3 => "Disque dur virtuel",
            4 => "Support d'installation et réseau",
            _ => "Résumé"
        };

        public string StepCaption =>
            $"Étape {(StepIndex + 1).ToString(CultureInfo.CurrentCulture)} sur {StepCount.ToString(CultureInfo.CurrentCulture)}";

        public bool IsLastStep => StepIndex == StepCount - 1;

        public string PrimaryButtonText => IsLastStep ? "Créer" : "Suivant";

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
                return ($"Une machine virtuelle nommée « {Name.Trim()} » existe déjà.", null);

            if (!_manager.IsQemuAvailable)
                return ("QEMU est introuvable : impossible de créer une machine virtuelle.", null);

            if (Directory.Exists(TargetFolder))
            {
                return (null, $"Le dossier {TargetFolder} existe déjà. Un suffixe numérique sera ajouté " +
                              "pour ne rien écraser.");
            }

            return (null, null);
        }

        private (string? Error, string? Advisory) ValidatePlatform()
        {
            if (CpuCount < 1)
                return ("Il faut au moins un processeur virtuel.", null);

            if (MemoryMb < 128)
                return ("Il faut au moins 128 Mo de mémoire.", null);

            var installation = _manager.Installation;
            if (installation is not null
                && Firmware != FirmwareType.Bios
                && !installation.SupportsUefi(SelectedProfile.Architecture, Firmware == FirmwareType.UefiSecureBoot))
            {
                return ($"Le microprogramme UEFI demandé est absent de {installation.InstallDirectory}. " +
                        "Choisissez BIOS ou installez les fichiers edk2.", null);
            }

            if (installation is not null
                && Firmware == FirmwareType.UefiSecureBoot
                && QemuArgumentBuilder.ResolveAccelerator(BuildPreviewConfig(), installation) == AcceleratorType.Whpx)
            {
                return ("Le démarrage sécurisé exige le mode SMM, que l'hyperviseur WHPX ne fournit pas : QEMU " +
                        "refuserait de démarrer. Choisissez UEFI sans démarrage sécurisé.", null);
            }

            var advisories = new List<string>();

            var hostMemory = HostCapabilities.TotalPhysicalMemoryMb;
            if (hostMemory > 0 && MemoryMb > HostCapabilities.RecommendedMaxGuestMemoryMb)
            {
                advisories.Add(
                    $"L'hôte dispose de {FormatMb(hostMemory)}. Allouer {FormatMb(MemoryMb)} risque de le faire " +
                    "recourir au fichier d'échange et de ralentir tout le système.");
            }

            if (CpuCount > HostCapabilities.LogicalProcessors)
            {
                advisories.Add(
                    $"L'hôte n'a que {HostCapabilities.LogicalProcessors.ToString(CultureInfo.CurrentCulture)} " +
                    "processeurs logiques. Au-delà, les vCPU se disputent le même matériel et les performances baissent.");
            }

            if (installation is not null && !installation.SupportsWhpx)
            {
                advisories.Add("L'accélération matérielle WHPX est indisponible : la VM tournera en émulation, " +
                               "environ dix fois plus lentement.");
            }

            return (null, advisories.Count > 0 ? string.Join(Environment.NewLine, advisories) : null);
        }

        private (string? Error, string? Advisory) ValidateDisk()
        {
            if (!CreateNewDisk)
            {
                if (string.IsNullOrWhiteSpace(ExistingDiskPath))
                    return ("Indiquez le fichier disque à rattacher.", null);
                if (!File.Exists(ExistingDiskPath))
                    return ($"Fichier introuvable : {ExistingDiskPath}", null);

                return (null, "Le disque est référencé à son emplacement actuel, il n'est pas copié. " +
                              "Le déplacer ou le supprimer rendra la VM indémarrable.");
            }

            if (DiskSizeGb < 1)
                return ("La taille du disque doit être d'au moins 1 Go.", null);

            if (DiskSizeGb > 8192)
                return ("La taille du disque est limitée à 8192 Go par cet assistant.", null);

            var free = HostCapabilities.GetFreeSpaceBytes(LibraryRoot);
            if (free >= 0 && DiskCapacityBytes > free)
            {
                return DiskFormat == DiskFormat.Qcow2
                    ? (null, $"Le disque déclare {DiskSizeGb.ToString(CultureInfo.CurrentCulture)} Go alors qu'il " +
                             $"reste {FormatBytes(free)} de libre. Le format qcow2 est creux : il n'occupera que " +
                             "l'espace réellement écrit, mais l'invité tombera en panne d'espace avant sa taille annoncée.")
                    : ($"Le format {DiskFormat} réserve immédiatement tout l'espace, or il ne reste que " +
                       $"{FormatBytes(free)} sur le volume.", null);
            }

            return (null, null);
        }

        private (string? Error, string? Advisory) ValidateMedia()
        {
            if (!string.IsNullOrWhiteSpace(IsoPath) && !File.Exists(IsoPath))
                return ($"Image ISO introuvable : {IsoPath}", null);

            if (UseUnattended && (IsLinuxUnattended
                    ? UbuntuUnattendedSetup.WhyUserNameIsRefused(UnattendedUser)
                    : WindowsUnattendedSetup.WhyUserNameIsRefused(UnattendedUser)) is { } refused)
                return (refused, null);
            if (UseUnattended && IsLinuxUnattended && UnattendedPassword.Length == 0)
                return ("Ubuntu demande un mot de passe pour ouvrir une session : indiquez-en un.", null);

            if (!string.IsNullOrWhiteSpace(SecondaryIsoPath) && !File.Exists(SecondaryIsoPath))
                return ($"Second ISO introuvable : {SecondaryIsoPath}", null);

            var advisories = new List<string>();

            if (string.IsNullOrWhiteSpace(IsoPath))
            {
                advisories.Add("Sans support d'installation, la VM démarrera sur un disque vide et s'arrêtera au " +
                               "microprogramme. Vous pourrez ajouter un ISO plus tard.");
            }

            if (UseUnattended && !IsLinuxUnattended && string.IsNullOrWhiteSpace(SecondaryIsoPath))
            {
                advisories.Add("Sans l'ISO virtio-win en second lecteur, Windows s'installera sans ses pilotes virtio ni " +
                               "l'agent invité : pas de dossiers partagés, ni de résolution qui suit la fenêtre.");
            }
            else if (SelectedProfile.SuggestsVirtioDrivers && string.IsNullOrWhiteSpace(SecondaryIsoPath))
            {
                advisories.Add("Pour ce système, l'ISO virtio-win en second lecteur permet d'utiliser les " +
                               "périphériques virtio (réseau et disque plus rapides). Facultatif ici, car le " +
                               "disque est configuré sur un bus reconnu nativement.");
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
            $"Hôte : {HostCapabilities.LogicalProcessors.ToString(CultureInfo.CurrentCulture)} processeurs logiques, " +
            $"{FormatMb(HostCapabilities.TotalPhysicalMemoryMb)} de mémoire.";

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
            ? "Ubuntu s'installe sans rien demander : tout le disque, ce compte (administrateur par sudo), la langue, le clavier et le fuseau horaire de ce PC, l'agent invité et l'agent de presse-papiers (dossiers partagés et copier-coller d'emblée). Il faut une carte en NAT : les paquets viennent d'Internet."
            : "Windows s'installe sans rien demander : l'édition Professionnel, tout le disque, ce compte (administrateur, ouvert automatiquement la première fois), la langue, le clavier et le fuseau horaire de ce PC. Puis les pilotes virtio et l'agent invité, si l'ISO virtio-win est en second lecteur.";

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
                builder.AppendLine($"Nom : {Name.Trim()}");
                builder.AppendLine($"Emplacement : {TargetFolder}");
                builder.AppendLine($"Système invité : {SelectedProfile.DisplayName} ({SelectedProfile.Architecture})");
                builder.AppendLine($"Microprogramme : {DescribeFirmware(Firmware)}");
                builder.AppendLine($"Processeurs virtuels : {CpuCount.ToString(CultureInfo.CurrentCulture)}");
                builder.AppendLine($"Mémoire : {FormatMb(MemoryMb)}");

                builder.AppendLine(CreateNewDisk
                    ? $"Disque : nouveau, {DiskSizeGb.ToString(CultureInfo.CurrentCulture)} Go, " +
                      $"{DiskFormat.ToString().ToLowerInvariant()}, bus {DescribeBus(DiskBus)}"
                    : $"Disque : existant, {ExistingDiskPath}, bus {DescribeBus(DiskBus)}");

                builder.AppendLine(string.IsNullOrWhiteSpace(IsoPath)
                    ? "Support d'installation : aucun"
                    : $"Support d'installation : {IsoPath}{(BootFromIso ? " (démarrage dessus)" : string.Empty)}");

                if (!string.IsNullOrWhiteSpace(SecondaryIsoPath))
                    builder.AppendLine($"Second lecteur : {SecondaryIsoPath}");

                builder.AppendLine($"Réseau : {NetworkChoices.FirstOrDefault(c => c.Key == NetworkConnection)?.Label ?? DescribeNetwork(NetworkMode)}");
                builder.Append($"Horloge matérielle : {(RtcLocalTime ? "heure locale" : "UTC")}");

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
                    return "(QEMU introuvable)";

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
                    return $"(aperçu indisponible : {ex.Message})";
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
                : null
        };

        // ---- Display helpers ----

        public static string DescribeFirmware(FirmwareType firmware) => firmware switch
        {
            FirmwareType.Bios => "BIOS (héritage)",
            FirmwareType.Uefi => "UEFI",
            FirmwareType.UefiSecureBoot => "UEFI avec démarrage sécurisé",
            _ => firmware.ToString()
        };

        public static string DescribeBus(DiskBus bus) => bus switch
        {
            DiskBus.VirtioBlk => "virtio (rapide, pilote requis)",
            DiskBus.Sata => "SATA (reconnu partout)",
            DiskBus.Nvme => "NVMe",
            DiskBus.Ide => "IDE (héritage)",
            DiskBus.LsiSas => "LSI SAS (comme VMware)",
            DiskBus.Pvscsi => "PVSCSI (VMware, pilote requis)",
            _ => bus.ToString()
        };

        public static string DescribeNetwork(NetworkMode mode) => mode switch
        {
            NetworkMode.Nat => "NAT (partage la connexion de l'hôte)",
            NetworkMode.Bridged => "Ponté (pilote TAP requis)",
            NetworkMode.Internal => "Segment multicast (ancien)",
            NetworkMode.Network => "Réseau virtuel (Privé, Interne…)",
            NetworkMode.None => "Aucun",
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

        private static string FormatMb(int megabytes) => megabytes >= 1024
            ? $"{(megabytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture)} Gio"
            : $"{megabytes.ToString(CultureInfo.CurrentCulture)} Mio";

        private static string FormatBytes(long bytes)
        {
            string[] units = { "o", "Kio", "Mio", "Gio", "Tio" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
        }

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
