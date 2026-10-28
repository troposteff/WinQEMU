using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

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
        /// <summary>The virtual network a <see cref="NetworkMode.Network"/> card is plugged into.</summary>
        public string NetworkId { get; init; } = string.Empty;
        public NicModel NicModel { get; init; } = NicModel.VirtioNet;

        /// <summary>For an unattended Windows installation, its answers; null to install by hand.</summary>
        public UnattendedWindows? Unattended { get; init; }

        /// <summary>For an unattended Ubuntu installation, its answers; null to install by hand.</summary>
        public UnattendedLinux? UnattendedLinux { get; init; }
        public bool RtcLocalTime { get; init; }
    }

    /// <summary>
    /// The files of a linked clone, laid down but not yet recorded. Committing it means pushing each
    /// overlay onto the source's disk and saving the source, then saving the clone — in that order, so the
    /// clone never exists while the source still writes to the image the clone reads.
    /// </summary>
    public sealed record LinkedClonePlan(
        VmConfig Source,
        VmConfig CloneConfig,
        IReadOnlyList<(DiskConfig Disk, string OverlayFileName)> SourceOverlays);

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

            if (request.Unattended is { } unattended && config.BootFromIso)
            {
                WindowsUnattendedSetup.WriteTo(QemuArgumentBuilder.GetWindowsSetupFolder(config), unattended);
                config.UnattendedSetup = UnattendedSetupState.Pending;
            }
            else if (request.UnattendedLinux is { } linux && !string.IsNullOrWhiteSpace(request.IsoPath))
            {
                // Read out of the ISO now, so the first start does not wait on a hundred megabytes.
                if (!UbuntuUnattendedSetup.WriteTo(QemuArgumentBuilder.GetWindowsSetupFolder(config), linux, request.IsoPath))
                    throw new InvalidOperationException(CoreText.LibraryUbuntuIsoWithoutInstaller);
                config.UnattendedSetup = UnattendedSetupState.Pending;
                // The installer's own kernel is booted for the first start; every later one is the disk's.
                config.BootFromIso = false;
            }

            if (request.NetworkMode != NetworkMode.None)
            {
                config.NetworkAdapters.Add(new NetworkAdapterConfig
                {
                    Id = "net0",
                    Mode = request.NetworkMode,
                    NetworkId = request.NetworkId,
                    Model = request.NicModel,
                    MacAddress = NetworkAdapterConfig.GenerateMacAddress()
                });
            }

            Save(config);
            return config;
        }

        /// <summary>
        /// Makes an independent copy of a stopped VM under a new name: same hardware, its own folder,
        /// identity and MAC addresses, and a copy of every disk as it is now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each disk is copied with <c>qemu-img convert</c>, never as files. A live-snapshot overlay records
        /// its base by absolute path, so a copied overlay would go on reading the original's image, and the
        /// clone would change whenever the original merged or reverted a snapshot. Converting reads through
        /// the chain and writes one standalone image — at the cost of the snapshots, which describe the
        /// original's past, not the clone's.
        /// </para>
        /// <para>
        /// Disks attached from outside the VM folder are copied too: sharing one writable image between
        /// two machines corrupts it the first time both run.
        /// </para>
        /// <para>The caller guarantees the source is not running: qemu-img cannot read an image QEMU holds open.</para>
        /// </remarks>
        /// <param name="progress">Receives the completed fraction of the whole copy, from 0 to 1.</param>
        public async Task<VmConfig> CloneAsync(
            VmConfig source,
            string name,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_imgService is null)
                throw new QemuNotAvailableException("QEMU is required to clone a virtual machine.");

            if (ValidateName(name) is { } nameProblem)
                throw new ArgumentException(nameProblem, nameof(name));

            // Measured before anything is created, so a missing disk fails without leaving a half-made
            // folder behind, and so progress can be weighted by how much each disk has to copy.
            var sizes = new List<long>();
            foreach (var disk in source.Disks)
            {
                var path = source.ResolvePath(disk.FileName);
                if (!File.Exists(path))
                    throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, CoreText.LibraryDiskNotFound, disk.Id, path), path);

                var info = await _imgService.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                sizes.Add(Math.Max(1, info.VirtualSizeBytes));
            }

            var clone = CopyDescriptor(source, name);
            var folder = AllocateFolder(clone.Name);
            Directory.CreateDirectory(folder);
            clone.FolderPath = folder;

            try
            {
                var total = (double)sizes.Sum();
                var done = 0d;

                for (var index = 0; index < clone.Disks.Count; index++)
                {
                    // Checked here and not only through the process wait: Process.WaitForExitAsync does not
                    // throw for a process that has already exited, so a cancel landing between two disks
                    // could be missed by a fast conversion and the clone completed anyway.
                    cancellationToken.ThrowIfCancellationRequested();

                    var disk = clone.Disks[index];
                    var sourcePath = source.ResolvePath(source.Disks[index].FileName);
                    var fileName = $"{disk.Id}.{DiskExtension(disk.Format)}";

                    var weight = sizes[index];
                    var before = done;
                    await _imgService.ConvertAsync(
                        sourcePath,
                        Path.Combine(folder, fileName),
                        disk.Format,
                        compress: false,
                        progress is null ? null : new InlineProgress(f => progress.Report((before + f * weight) / total)),
                        cancellationToken).ConfigureAwait(false);
                    done += weight;

                    // Relative, so the clone's folder is movable as a unit like any other.
                    disk.FileName = fileName;
                    disk.BackingFileName = string.Empty;
                    disk.DeeperBackingFiles = new List<string>();
                    // Copying a linked clone in full reads its base too, so the copy depends on nothing.
                    disk.LinkedBaseFileName = string.Empty;
                }

                // The last disk's conversion may have outrun a cancel just as well.
                cancellationToken.ThrowIfCancellationRequested();

                // The UEFI variable store holds the boot entries the guest installer wrote. Without it the
                // firmware starts from a blank store and only finds an OS through the removable-media
                // fallback path, which not every installer populates.
                var vars = QemuArgumentBuilder.GetUefiVarsPath(source);
                if (File.Exists(vars))
                    File.Copy(vars, QemuArgumentBuilder.GetUefiVarsPath(clone));

                Save(clone);
                progress?.Report(1d);
                return clone;
            }
            catch
            {
                // The folder was allocated fresh for this clone, so nothing in it belongs to anyone else.
                TryDeleteFolder(folder);
                throw;
            }
        }

        /// <summary>
        /// Creates a VM from a machine of another hypervisor: each disk converted to qcow2 in a new folder,
        /// the hardware as the plan describes it. The source files are only read.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Converted rather than referenced: VMware and VirtualBox keep their machine in a chain of images
        /// (snapshot deltas) that <c>qemu-img convert</c> reads through, and the result is a disk this
        /// application can snapshot like any other. The source machine keeps working in its hypervisor.
        /// </para>
        /// <para>
        /// The UEFI variable store does not come across — VMware and VirtualBox each keep it in their own
        /// format — so the boot entries are lost and the firmware falls back to the disk's removable-media
        /// path, which Windows and most distributions fill in.
        /// </para>
        /// </remarks>
        public async Task<VmConfig> ImportAsync(
            Import.ImportPlan plan,
            string name,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_imgService is null)
                throw new QemuNotAvailableException("QEMU is required to import a virtual machine.");

            if (plan.Blocker is not null)
                throw new InvalidOperationException(plan.Blocker);

            if (ValidateName(name) is { } nameProblem)
                throw new ArgumentException(nameProblem, nameof(name));

            var profile = plan.Profile;
            var config = new VmConfig
            {
                Name = name.Trim(),
                Description = string.Format(CultureInfo.CurrentCulture, CoreText.LibraryImportedDescription,
                    SourceName(plan.Source), DateTime.Now.ToString("d", CultureInfo.CurrentCulture), plan.SourcePath),
                GuestOs = plan.GuestOs,
                Architecture = GuestArchitecture.X86_64,
                Firmware = plan.Firmware,
                CpuCount = plan.CpuCount,
                CoresPerSocket = plan.CoresPerSocket,
                MemoryMb = plan.MemoryMb,
                IsoPath = plan.IsoPath,
                BootFromIso = false,
                RtcLocalTime = profile.RtcLocalTime
            };

            var folder = AllocateFolder(config.Name);
            Directory.CreateDirectory(folder);
            config.FolderPath = folder;

            try
            {
                var total = (double)plan.Disks.Sum(d => Math.Max(1, d.CapacityBytes));
                var done = 0d;

                for (var index = 0; index < plan.Disks.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = plan.Disks[index];
                    var id = $"disk{index.ToString(CultureInfo.InvariantCulture)}";
                    var fileName = id + ".qcow2";
                    var weight = Math.Max(1, source.CapacityBytes);
                    var before = done;

                    await _imgService.ConvertAsync(
                        source.ImageSpec,
                        Path.Combine(folder, fileName),
                        DiskFormat.Qcow2,
                        compress: false,
                        progress is null ? null : new InlineProgress(f => progress.Report((before + f * weight) / total)),
                        cancellationToken).ConfigureAwait(false);
                    done += weight;

                    config.Disks.Add(new DiskConfig
                    {
                        Id = id,
                        FileName = fileName,
                        Format = DiskFormat.Qcow2,
                        Bus = source.Bus,
                        CapacityBytes = source.CapacityBytes,
                        // The first disk of the plan is the one the source booted from.
                        BootIndex = index == 0 ? 0 : -1
                    });
                }

                cancellationToken.ThrowIfCancellationRequested();

                for (var index = 0; index < plan.Adapters.Count; index++)
                {
                    var adapter = plan.Adapters[index];
                    config.NetworkAdapters.Add(new NetworkAdapterConfig
                    {
                        Id = $"net{index.ToString(CultureInfo.InvariantCulture)}",
                        Mode = adapter.Mode,
                        Model = adapter.Model,
                        // The source's address when it had one: the guest's network settings follow it.
                        MacAddress = adapter.MacAddress.Length > 0 ? adapter.MacAddress : NetworkAdapterConfig.GenerateMacAddress()
                    });
                }

                Save(config);
                progress?.Report(1d);
                return config;
            }
            catch
            {
                TryDeleteFolder(folder);
                throw;
            }
        }

        private static string SourceName(Import.ImportSource source) => source switch
        {
            Import.ImportSource.VMware => CoreText.LibraryImportedFromVmware,
            Import.ImportSource.VirtualBox => CoreText.LibraryImportedFromVirtualBox,
            Import.ImportSource.Ovf => CoreText.LibraryImportedFromOvf,
            _ => CoreText.LibraryImportedFromDisk
        };

        /// <summary>
        /// Lays the files of a linked clone of a stopped VM: one qcow2 overlay per disk in a new folder, on
        /// top of the image the source writes to now, plus a fresh overlay for the source itself so it never
        /// writes to that image again. Nothing is recorded yet — see <see cref="LinkedClonePlan"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Freezing the source is what makes the clone safe at all: the clone reads every block it has not
        /// written from the source's image, so the source must move to a new layer. That layer is a live
        /// snapshot like any other, named after the clone, and shows up in the source's snapshot list as the
        /// state the clone started from. <see cref="LinkedCloneGuard"/> then refuses to undo or merge it.
        /// </para>
        /// <para>
        /// qcow2 only, on both sides: the source gains an overlay, and the descriptor holds one format per
        /// disk — the same reason live snapshots exclude other formats.
        /// </para>
        /// <para>
        /// The clone's overlay names its base by a path relative to the clone's folder when possible, as qemu
        /// resolves a relative backing path against the overlay's own directory: moving the whole library
        /// then keeps the pair working, where an absolute path would break it.
        /// </para>
        /// </remarks>
        public async Task<LinkedClonePlan> PrepareLinkedCloneAsync(
            VmConfig source,
            string name,
            CancellationToken cancellationToken = default)
        {
            if (_imgService is null)
                throw new QemuNotAvailableException("QEMU is required to clone a virtual machine.");

            if (ValidateName(name) is { } nameProblem)
                throw new ArgumentException(nameProblem, nameof(name));

            if (WhyNoLinkedClone(source) is { } refusal)
                throw new InvalidOperationException(refusal);

            var bases = new List<(string Path, long Size)>();
            foreach (var disk in source.Disks)
            {
                var path = source.ResolvePath(disk.FileName);
                if (!File.Exists(path))
                    throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, CoreText.LibraryDiskNotFound, disk.Id, path), path);

                var info = await _imgService.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                bases.Add((path, info.VirtualSizeBytes));
            }

            var clone = CopyDescriptor(source, name);
            var snapshotName = UniqueLinkedSnapshotName(source, clone.Name);
            var folder = AllocateFolder(clone.Name);
            Directory.CreateDirectory(folder);
            clone.FolderPath = folder;

            var sourceOverlays = new List<(DiskConfig Disk, string OverlayFileName)>();
            try
            {
                for (var index = 0; index < clone.Disks.Count; index++)
                {
                    var disk = clone.Disks[index];
                    var (basePath, size) = bases[index];

                    var fileName = $"{disk.Id}.qcow2";
                    var linkedBase = Path.GetRelativePath(folder, basePath);
                    await _imgService.CreateAsync(
                        Path.Combine(folder, fileName), DiskFormat.Qcow2, size, linkedBase, cancellationToken).ConfigureAwait(false);

                    disk.FileName = fileName;
                    disk.BackingFileName = string.Empty;
                    disk.DeeperBackingFiles = new List<string>();
                    disk.LinkedBaseFileName = linkedBase;

                    var sourceDisk = source.Disks[index];
                    var overlayFileName = $"{sourceDisk.Id}-{snapshotName}.qcow2";
                    await _imgService.CreateAsync(
                        Path.Combine(source.FolderPath, overlayFileName),
                        DiskFormat.Qcow2,
                        size,
                        Path.GetRelativePath(source.FolderPath, basePath),
                        cancellationToken).ConfigureAwait(false);
                    sourceOverlays.Add((sourceDisk, overlayFileName));
                }

                var vars = QemuArgumentBuilder.GetUefiVarsPath(source);
                if (File.Exists(vars))
                    File.Copy(vars, QemuArgumentBuilder.GetUefiVarsPath(clone));

                return new LinkedClonePlan(source, clone, sourceOverlays);
            }
            catch
            {
                AbandonLinkedClone(new LinkedClonePlan(source, clone, sourceOverlays));
                throw;
            }
        }

        /// <summary>Deletes the files a plan laid down. The source's descriptor must not reference them.</summary>
        public static void AbandonLinkedClone(LinkedClonePlan plan)
        {
            foreach (var (_, overlay) in plan.SourceOverlays)
            {
                try
                {
                    File.Delete(Path.Combine(plan.Source.FolderPath, overlay));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreferenced overlay only takes space; the error that got us here matters more.
                }
            }

            TryDeleteFolder(plan.CloneConfig.FolderPath);
        }

        /// <summary>Why the machine cannot be linked-cloned, or null when it can.</summary>
        public static string? WhyNoLinkedClone(VmConfig source)
        {
            var others = source.Disks.Where(d => d.Format != DiskFormat.Qcow2).ToList();
            return others.Count == 0
                ? null
                : string.Format(CultureInfo.CurrentCulture, CoreText.LibraryLinkedCloneNeedsQcow2,
                    string.Join(", ", others.Select(d => $"{d.Id} ({d.Format.ToString().ToLowerInvariant()})")));
        }

        /// <summary>
        /// "Base of Debian (copy)", "Base de Debian (copie)" in French: the live snapshot the source gains, named
        /// after the clone so the list says why it is there, in the interface's language when it is taken; nothing
        /// reads the name back. ASCII only, as every snapshot name (see
        /// <see cref="QemuImgService.ValidateSnapshotName"/>), and unused by any level of the source's chains.
        /// </summary>
        private static string UniqueLinkedSnapshotName(VmConfig source, string cloneName)
        {
            var folded = new string(cloneName.Normalize(System.Text.NormalizationForm.FormD)
                    .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    .Select(c => c is < ' ' or > '~' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                    .ToArray())
                .Trim();

            var stem = string.Format(CultureInfo.InvariantCulture, CoreText.LibraryLinkedCloneSnapshotName, folded.Length == 0 ? "clone" : folded);
            var candidate = stem;
            for (var index = 2; Taken(candidate); index++)
                candidate = $"{stem} {index.ToString(CultureInfo.InvariantCulture)}";

            return candidate;

            bool Taken(string snapshot) => source.Disks.Any(disk =>
                disk.ChainFromTop.Any(file => DiskConfig.SnapshotNameOf(disk.Id, file) == snapshot)
                || File.Exists(Path.Combine(source.FolderPath, $"{disk.Id}-{snapshot}.qcow2")));
        }

        /// <summary>
        /// The source's descriptor under a new name and identity. Disks still name the source's files; the
        /// caller repoints them.
        /// </summary>
        private static VmConfig CopyDescriptor(VmConfig source, string name)
        {
            var clone = JsonSerializer.Deserialize<VmConfig>(JsonSerializer.Serialize(source, SerializerOptions), SerializerOptions)
                        ?? throw new InvalidOperationException("The VM descriptor could not be copied.");

            clone.Id = Guid.NewGuid();
            clone.Name = name.Trim();
            clone.CreatedUtc = DateTime.UtcNow;
            clone.LastStartedUtc = null;

            // Two guests with one MAC on the same segment fight over it. An empty MAC stays empty: QEMU
            // assigns one at launch, exactly as it did for the original.
            foreach (var adapter in clone.NetworkAdapters.Where(a => !string.IsNullOrWhiteSpace(a.MacAddress)))
                adapter.MacAddress = NetworkAdapterConfig.GenerateMacAddress();

            return clone;
        }

        private static void TryDeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A killed qemu-img may hold its output a moment longer; the folder is then left for the
                // user to remove rather than masking the error that actually stopped the clone.
            }
        }

        /// <summary>
        /// Reports synchronously on the caller's thread. <see cref="Progress{T}"/> would post each report to
        /// the context it was created on, and the one wrapping it does that already.
        /// </summary>
        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _report;
            public InlineProgress(Action<double> report) => _report = report;
            public void Report(double value) => _report(value);
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
                return CoreText.LibraryNameRequired;

            var trimmed = name.Trim();
            if (trimmed.Length > 100)
                return CoreText.LibraryNameTooLong;

            var invalid = trimmed.Where(c => Path.GetInvalidFileNameChars().Contains(c)).Distinct().ToArray();
            if (invalid.Length > 0)
                return string.Format(CultureInfo.CurrentCulture, CoreText.LibraryNameInvalidCharacters,
                    string.Join(" ", invalid.Select(c => string.Format(CultureInfo.CurrentCulture, CoreText.QuotedName, c))));

            // Win32 resolves a reserved device name from the part before the first dot, so "CON.vm"
            // is refused just as "CON" is -- and the descriptor would be "CON.vm.wqvm", still CON.
            var baseName = trimmed.Split('.')[0].TrimEnd();
            if (ReservedDeviceNames.Contains(baseName))
                return string.Format(CultureInfo.CurrentCulture, CoreText.LibraryNameReserved, baseName);

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

        /// <summary>
        /// The machine's name as a folder name, accents folded and anything else non-ASCII replaced:
        /// qemu-system cannot open a non-ASCII path (see QemuArgumentBuilder.QemuReadablePath), and the 8.3
        /// short name that works around it is not made on every volume.
        /// </summary>
        public static string SanitizeFolderName(string name)
        {
            var cleaned = new string(name.Trim()
                .Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) || !char.IsAscii(c) ? '_' : c)
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
