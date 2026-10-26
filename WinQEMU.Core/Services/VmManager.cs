using System.Collections.ObjectModel;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Application-level entry point to the core: owns QEMU discovery, the VM library and the
    /// live sessions. The UI talks to this and never to QEMU directly.
    /// </summary>
    public sealed class VmManager : ObservableBase, IAsyncDisposable
    {
        private readonly Dictionary<Guid, VmSession> _sessions = new();

        public VmManager(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; }

        public ObservableCollection<VmConfig> VirtualMachines { get; } = new();

        /// <summary>Non-fatal problems from the last <see cref="InitializeAsync"/>, suitable for an info bar.</summary>
        public ObservableCollection<string> Warnings { get; } = new();

        private QemuDetectionResult? _detection;
        public QemuDetectionResult? Detection { get => _detection; private set => SetProperty(ref _detection, value); }

        /// <summary>Null until QEMU is found. Every QEMU-dependent feature must check this first.</summary>
        public QemuInstallation? Installation => Detection?.Installation;

        public bool IsQemuAvailable => Installation is not null;

        public QemuImgService? DiskService { get; private set; }

        public VmLibrary? Library { get; private set; }

        /// <summary>
        /// Detects QEMU and loads the library. Deliberately does not throw when QEMU is missing:
        /// the shell still has to run so it can explain the problem and let the user point at an install.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Warnings.Clear();

            // No ConfigureAwait(false) anywhere in this method: everything after the await touches
            // data-bound collections and raises PropertyChanged, which must happen on the thread
            // that owns them. Resuming on the thread pool here silently loses UI updates.
            Detection = await QemuLocator.DetectAsync(Settings.QemuDirectory, cancellationToken);

            RaisePropertyChanged(nameof(Installation));
            RaisePropertyChanged(nameof(IsQemuAvailable));

            if (Installation is not null)
                DiskService = new QemuImgService(Installation);

            foreach (var rejected in Detection.Rejected)
                Warnings.Add($"{rejected.Directory}: {rejected.Reason}");

            Library = new VmLibrary(Settings.EffectiveLibraryRoot, DiskService);
            ReloadLibrary();
        }

        /// <summary>
        /// Moves the library to another folder and rescans. Existing VMs are left where they are —
        /// this changes where new machines are created and where the shell looks, it does not migrate
        /// anything, because moving multi-gigabyte disks behind the user's back would be worse.
        /// </summary>
        public void SetLibraryRoot(string path)
        {
            Settings.LibraryRoot = path;
            Settings.Save();

            Library = new VmLibrary(Settings.EffectiveLibraryRoot, DiskService);
            ReloadLibrary();
        }

        public void ReloadLibrary()
        {
            if (Library is null)
                return;

            var machines = Library.Scan(out var warnings);
            foreach (var warning in warnings)
                Warnings.Add(warning);

            // Keep configs that already have a live session, so restarting the scan never orphans
            // a running VM from its entry in the list.
            VirtualMachines.Clear();
            foreach (var machine in machines)
            {
                if (_sessions.TryGetValue(machine.Id, out var session) && session.IsRunning)
                    VirtualMachines.Add(session.Config);
                else
                    VirtualMachines.Add(machine);
            }
        }

        public async Task<VmConfig> CreateVirtualMachineAsync(
            CreateVmRequest request,
            CancellationToken cancellationToken = default)
        {
            var library = Library ?? throw new InvalidOperationException("The VM library is not initialised.");
            // Resumes on the caller's context on purpose — VirtualMachines is data-bound.
            var config = await library.CreateAsync(request, cancellationToken);
            VirtualMachines.Add(config);
            return config;
        }

        public void Save(VmConfig config) => Library?.Save(config);

        /// <summary>Returns the session for a VM, creating a stopped one on first request.</summary>
        public VmSession GetSession(VmConfig config)
        {
            if (_sessions.TryGetValue(config.Id, out var existing))
                return existing;

            var installation = Installation
                ?? throw new QemuNotAvailableException("QEMU was not found, so no VM can be started.");

            var session = new VmSession(config, installation);
            _sessions[config.Id] = session;
            SessionCreated?.Invoke(this, session);
            return session;
        }

        /// <summary>Raised when a VM gets its session, so services spanning all VMs can follow it.</summary>
        public event EventHandler<VmSession>? SessionCreated;

        public VmSession? FindSession(VmConfig config) =>
            _sessions.TryGetValue(config.Id, out var session) ? session : null;

        public IEnumerable<VmSession> RunningSessions => _sessions.Values.Where(s => s.IsRunning);

        public async Task DeleteVirtualMachineAsync(
            VmConfig config,
            bool deleteFiles,
            CancellationToken cancellationToken = default)
        {
            if (_sessions.TryGetValue(config.Id, out var session))
            {
                // Resumes on the caller's context on purpose — VirtualMachines is data-bound.
                if (session.IsRunning)
                    await session.PowerOffAsync(cancellationToken);

                _sessions.Remove(config.Id);
                await session.DisposeAsync();
            }

            Library?.Delete(config, deleteFiles);
            VirtualMachines.Remove(config);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in _sessions.Values.ToArray())
                await session.DisposeAsync().ConfigureAwait(false);

            _sessions.Clear();
        }
    }
}
