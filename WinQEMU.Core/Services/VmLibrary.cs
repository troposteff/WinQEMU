using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Services
{
    /// <summary>Parameters for creating a VM, mirroring what the creation wizard collects.</summary>
    public sealed class CreateVmRequest
    {
        public required string Name { get; init; }
        public string Description { get; init; } = string.Empty;
        public string GuestOs { get; init; } = string.Empty;
        public GuestArchitecture Architecture { get; init; } = GuestArchitecture.X86_64;
        public FirmwareType Firmware { get; init; } = FirmwareType.Uefi;
        public int CpuCount { get; init; } = 2;
        public int MemoryMb { get; init; } = 4096;

        /// <summary>Size of the disk to create. Ignored when <see cref="ExistingDiskPath"/> is set.</summary>
        public long DiskCapacityBytes { get; init; } = 64L * 1024 * 1024 * 1024;
        public DiskFormat DiskFormat { get; init; } = DiskFormat.Qcow2;
        public DiskBus DiskBus { get; init; } = DiskBus.VirtioBlk;

        /// <summary>
        /// Attach this image instead of creating one. Referenced in place rather than copied — the
        /// image may be tens of gigabytes, and silently duplicating it would be a nasty surprise.
        /// </summary>
        public string ExistingDiskPath { get; init; } = string.Empty;

        public string IsoPath { get; init; } = string.Empty;

        /// <summary>Second optical drive, typically the virtio-win driver ISO during Windows setup.</summary>
        public string SecondaryIsoPath { get; init; } = string.Empty;

        public bool BootFromIso { get; init; } = true;
        public NetworkMode NetworkMode { get; init; } = NetworkMode.Nat;
        public NicModel NicModel { get; init; } = NicModel.VirtioNet;
        public bool RtcLocalTime { get; init; }
    }

    /// <summary>
    /// Reads and writes the VM library on disk. Each VM lives in its own folder holding a
    /// <c>.wqvm</c> descriptor plus its disks, so a VM can be moved or backed up as a unit.
    /// </summary>
    public sealed class VmLibrary
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly QemuImgService? _imgService;

        public VmLibrary(string rootPath, QemuImgService? imgService)
        {
            RootPath = rootPath;
            _imgService = imgService;
        }

        public string RootPath { get; }

        /// <summary>Loads every VM descriptor under the library root. Unreadable descriptors are skipped, not fatal.</summary>
        public IReadOnlyList<VmConfig> Scan(out IReadOnlyList<string> warnings)
        {
            var problems = new List<string>();
            var machines = new List<VmConfig>();

            if (!Directory.Exists(RootPath))
            {
                warnings = problems;
                return machines;
            }

            foreach (var descriptor in Directory.EnumerateFiles(
                         RootPath, "*" + VmConfig.FileExtension, SearchOption.AllDirectories))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<VmConfig>(File.ReadAllText(descriptor), SerializerOptions);
                    if (config is null)
                    {
                        problems.Add($"{descriptor}: the descriptor is empty.");
                        continue;
                    }

                    // FolderPath is derived from where the file actually is, never persisted, so a
                    // moved or renamed folder resolves its disks correctly.
                    config.FolderPath = Path.GetDirectoryName(descriptor)!;
                    machines.Add(config);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    problems.Add($"{descriptor}: {ex.Message}");
                }
            }

            warnings = problems;
            return machines.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public void Save(VmConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.FolderPath))
                throw new InvalidOperationException($"VM '{config.Name}' has no folder assigned.");

            // The name becomes the descriptor file name, so an unusable one has to be refused here and
            // not only in the creation wizard: everything that edits Name reaches this method.
            if (ValidateName(config.Name) is { } nameProblem)
                throw new ArgumentException(nameProblem, nameof(config));

            Directory.CreateDirectory(config.FolderPath);

            // Write first, delete the stale descriptors afterwards. The reverse order lost the machine
            // outright: renaming it to something the filesystem rejects deleted the only descriptor and
            // then failed to write the new one, leaving the disks with nothing left to load them from.
            var target = config.DescriptorPath;
            File.WriteAllText(target, JsonSerializer.Serialize(config, SerializerOptions));

            // Renaming a VM renames its descriptor, so stale files do not accumulate and reappear on scan.
            foreach (var existing in Directory.EnumerateFiles(config.FolderPath, "*" + VmConfig.FileExtension))
            {
                if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
                    File.Delete(existing);
            }
        }

        public async Task<VmConfig> CreateAsync(CreateVmRequest request, CancellationToken cancellationToken = default)
        {
            if (_imgService is null)
                throw new QemuNotAvailableException("QEMU is required to create a virtual machine.");

            if (ValidateName(request.Name) is { } nameProblem)
                throw new ArgumentException(nameProblem, nameof(request));

            var folder = AllocateFolder(request.Name);
            Directory.CreateDirectory(folder);

            var config = new VmConfig
            {
                Name = request.Name.Trim(),
                Description = request.Description,
                GuestOs = request.GuestOs,
                Architecture = request.Architecture,
                Firmware = request.Firmware,
                CpuCount = request.CpuCount,
                MemoryMb = request.MemoryMb,
                IsoPath = request.IsoPath,
                SecondaryIsoPath = request.SecondaryIsoPath,
                BootFromIso = request.BootFromIso && !string.IsNullOrWhiteSpace(request.IsoPath),
                RtcLocalTime = request.RtcLocalTime,
                FolderPath = folder
            };

            await AddSystemDiskAsync(config, request, folder, cancellationToken).ConfigureAwait(false);

            if (request.NetworkMode != NetworkMode.None)
            {
                config.NetworkAdapters.Add(new NetworkAdapterConfig
                {
                    Id = "net0",
                    Mode = request.NetworkMode,
                    Model = request.NicModel,
                    MacAddress = NetworkAdapterConfig.GenerateMacAddress()
                });
            }

            Save(config);
            return config;
        }

        private async Task AddSystemDiskAsync(
            VmConfig config,
            CreateVmRequest request,
            string folder,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.ExistingDiskPath))
            {
                var existing = Path.GetFullPath(request.ExistingDiskPath);
                if (!File.Exists(existing))
                    throw new FileNotFoundException($"The disk image was not found: {existing}", existing);

                // Read the real geometry rather than trusting the wizard: the image already exists.
                var info = await _imgService!.GetInfoAsync(existing, cancellationToken).ConfigureAwait(false);

                config.Disks.Add(new DiskConfig
                {
                    Id = "disk0",
                    // Absolute: the image lives outside the VM folder, so it cannot be relative to it.
                    FileName = existing,
                    Format = ParseFormat(info.Format),
                    Bus = request.DiskBus,
                    CapacityBytes = info.VirtualSizeBytes,
                    BootIndex = 0
                });
                return;
            }

            if (request.DiskCapacityBytes <= 0)
                return;

            var diskFileName = "disk0." + DiskExtension(request.DiskFormat);
            await _imgService!.CreateAsync(
                Path.Combine(folder, diskFileName),
                request.DiskFormat,
                request.DiskCapacityBytes,
                backingFile: null,
                cancellationToken).ConfigureAwait(false);

            config.Disks.Add(new DiskConfig
            {
                Id = "disk0",
                // Stored relative so the whole folder can be moved or copied.
                FileName = diskFileName,
                Format = request.DiskFormat,
                Bus = request.DiskBus,
                CapacityBytes = request.DiskCapacityBytes,
                BootIndex = 0
            });
        }

        /// <summary>
        /// Every name Win32 still resolves to a DOS device, so none of them can be a file or a folder.
        /// </summary>
        /// <remarks>
        /// The console and printer names plus COM1-COM9 and LPT1-LPT9. The superscript forms are here
        /// because Win32 folds them onto the same devices: COM¹ opens COM1.
        /// </remarks>
        private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

        private static HashSet<string> BuildReservedDeviceNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL"
            };

            for (var index = 1; index <= 9; index++)
            {
                names.Add($"COM{index.ToString(CultureInfo.InvariantCulture)}");
                names.Add($"LPT{index.ToString(CultureInfo.InvariantCulture)}");
            }

            foreach (var superscript in new[] { '¹', '²', '³' })
            {
                names.Add($"COM{superscript}");
                names.Add($"LPT{superscript}");
            }

            return names;
        }

        /// <summary>
        /// Checks a proposed VM name. The name becomes a folder and a file name, so the filesystem's
        /// rules apply. Returns null when the name is acceptable, otherwise the reason.
        /// </summary>
        public static string? ValidateName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Le nom de la machine virtuelle est obligatoire.";

            var trimmed = name.Trim();
            if (trimmed.Length > 100)
                return "Le nom ne peut pas dépasser 100 caractères.";

            var invalid = trimmed.Where(c => Path.GetInvalidFileNameChars().Contains(c)).Distinct().ToArray();
            if (invalid.Length > 0)
                return $"Le nom ne peut pas contenir {string.Join(" ", invalid.Select(c => $"« {c} »"))}";

            // Win32 resolves a reserved device name from the part before the first dot, so "CON.vm"
            // is refused just as "CON" is -- and the descriptor would be "CON.vm.wqvm", still CON.
            var baseName = trimmed.Split('.')[0].TrimEnd();
            if (ReservedDeviceNames.Contains(baseName))
                return $"« {baseName} » est un nom réservé par Windows.";

            return null;
        }

        private static DiskFormat ParseFormat(string format) => format.ToLowerInvariant() switch
        {
            "qcow2" => DiskFormat.Qcow2,
            "raw" => DiskFormat.Raw,
            "vhdx" => DiskFormat.Vhdx,
            "vmdk" => DiskFormat.Vmdk,
            "vdi" => DiskFormat.Vdi,
            _ => DiskFormat.Qcow2
        };

        /// <summary>
        /// Removes a VM. <paramref name="deleteFiles"/> false only forgets it, leaving disks on disk —
        /// deleting gigabytes of guest data is not something to do implicitly.
        /// </summary>
        public void Delete(VmConfig config, bool deleteFiles)
        {
            if (deleteFiles)
            {
                if (Directory.Exists(config.FolderPath))
                    Directory.Delete(config.FolderPath, recursive: true);
                return;
            }

            if (File.Exists(config.DescriptorPath))
                File.Delete(config.DescriptorPath);
        }

        private string AllocateFolder(string name)
        {
            Directory.CreateDirectory(RootPath);

            var baseName = SanitizeFolderName(name);
            var candidate = Path.Combine(RootPath, baseName);
            var suffix = 2;
            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(RootPath, $"{baseName} ({suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
                suffix++;
            }

            return candidate;
        }

        private static string SanitizeFolderName(string name)
        {
            var cleaned = new string(name.Trim()
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                .ToArray())
                .TrimEnd('.', ' ');

            return cleaned.Length == 0 ? "Virtual Machine" : cleaned;
        }

        private static string DiskExtension(DiskFormat format) => format switch
        {
            DiskFormat.Qcow2 => "qcow2",
            DiskFormat.Raw => "img",
            DiskFormat.Vhdx => "vhdx",
            DiskFormat.Vmdk => "vmdk",
            DiskFormat.Vdi => "vdi",
            _ => "qcow2"
        };
    }
}
