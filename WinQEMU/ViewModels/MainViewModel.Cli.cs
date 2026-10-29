using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinQEMU.Core.Cli;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// The winqemu command's requests (Cli.CommandServer), run on the interface's thread like the buttons: a machine
    /// started from a terminal goes through StartMachineAsync, with its automatic snapshot, shared folders and
    /// console, exactly as if Démarrer had been pressed.
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>Raised by "winqemu show": the window comes forward.</summary>
        public event Action? ShowRequested;

        public async Task<CliResponse> RunCommandAsync(CliRequest request, CancellationToken cancellationToken)
        {
            var args = request.Arguments;
            try
            {
                return request.Command.ToLowerInvariant() switch
                {
                    "list" => List(args),
                    "status" => Status(args),
                    "start" => await StartFromCommandAsync(args, request.Password, cancellationToken),
                    "stop" => await StopFromCommandAsync(args, cancellationToken),
                    "poweroff" => await OnRunningAsync(args, s => s.PowerOffAsync(), CliText.PoweredOff),
                    "pause" => await OnRunningAsync(args, s => s.PauseAsync(), CliText.Paused),
                    "resume" => await OnRunningAsync(args, s => s.ResumeAsync(), CliText.Resumed),
                    "snapshot" => await SnapshotFromCommandAsync(args, request.Password),
                    "show" => Show(args),
                    _ => Misuse(string.Format(CultureInfo.CurrentCulture, CliText.UnknownCommand, request.Command))
                };
            }
            catch (CommandException ex)
            {
                return new CliResponse(ex.ExitCode, string.Empty, ex.Message);
            }
        }

        /// <summary>A refusal the command line prints as is.</summary>
        private sealed class CommandException : Exception
        {
            public CommandException(string message, int exitCode = CliProtocol.Failure) : base(message) => ExitCode = exitCode;

            public int ExitCode { get; }
        }

        private static CliResponse Ok(string output) => new(CliProtocol.Success, output);

        private static CliResponse Misuse(string message) => new(CliProtocol.Misuse, string.Empty, message + Environment.NewLine + CliText.SeeHelp);

        private static string Argument(string[] args, int index, string what) =>
            index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal)
                ? args[index]
                : throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.MissingArgument, what) + Environment.NewLine + CliText.SeeHelp,
                    CliProtocol.Misuse);

        private static string? Option(string[] args, string name)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        /// <summary>By its name in the library (case ignored), else by its id; two machines of that name must be told apart by id.</summary>
        private VmConfig FindMachine(string nameOrId)
        {
            var named = Manager.VirtualMachines.Where(vm => string.Equals(vm.Name, nameOrId, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (named.Count == 1)
                return named[0];
            if (named.Count > 1)
                throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.AmbiguousMachine, nameOrId,
                    string.Join(", ", named.Select(vm => vm.Id.ToString("N")))));
            return Manager.VirtualMachines.FirstOrDefault(vm =>
                       Guid.TryParse(nameOrId, out var id) && vm.Id == id)
                   ?? throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.UnknownMachine, nameOrId));
        }

        /// <summary>As VmManager.LiveSessions counts them: a machine asked to stop is still there until QEMU exits.</summary>
        private static bool IsLive(VmState state) => state is VmState.Starting or VmState.Running or VmState.Paused or VmState.Stopping;

        private VmState StateOf(VmConfig config) => Manager.FindSession(config)?.State ?? VmState.Stopped;

        private static string StateText(VmState state) => state switch
        {
            VmState.Stopped => ShellText.StateStopped,
            VmState.Starting => ShellText.StateStarting,
            VmState.Running => ShellText.StateRunning,
            VmState.Paused => ShellText.StatePaused,
            VmState.Stopping => ShellText.StateStopping,
            VmState.Error => CommonText.Error,
            _ => state.ToString()
        };

        /// <summary>"Work/Tests", or empty at the library's root.</summary>
        private string FolderPathOf(VmConfig config) =>
            string.Join("/", Library.PathOf(Library.FolderOf(config.Id)).Select(f => f.Name));

        private CliResponse List(string[] args)
        {
            var folder = Option(args, "--folder")?.Trim('/');
            var machines = Manager.VirtualMachines
                .Select(vm => (Config: vm, Folder: FolderPathOf(vm), State: StateOf(vm)))
                .Where(m => folder is null
                            || string.Equals(m.Folder, folder, StringComparison.CurrentCultureIgnoreCase)
                            || m.Folder.StartsWith(folder + "/", StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(m => m.Folder, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(m => m.Config.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (args.Contains("--json"))
            {
                // Stable names and values for scripts, whatever the interface's language.
                return Ok(JsonSerializer.Serialize(machines.Select(m => new
                {
                    name = m.Config.Name,
                    id = m.Config.Id.ToString("N"),
                    state = m.State.ToString().ToLowerInvariant(),
                    folder = m.Folder,
                    encrypted = m.Config.DisksEncrypted,
                    path = m.Config.FolderPath
                }), new JsonSerializerOptions { WriteIndented = true }));
            }

            if (machines.Count == 0)
                return Ok(CliText.NoMachines);
            var nameWidth = Math.Max(CliText.ColumnName.Length, machines.Max(m => m.Config.Name.Length));
            var stateWidth = Math.Max(CliText.ColumnState.Length, machines.Max(m => StateText(m.State).Length));
            var text = new StringBuilder();
            text.AppendLine($"{CliText.ColumnName.PadRight(nameWidth)}  {CliText.ColumnState.PadRight(stateWidth)}  {CliText.ColumnFolder}");
            foreach (var m in machines)
                text.AppendLine($"{m.Config.Name.PadRight(nameWidth)}  {StateText(m.State).PadRight(stateWidth)}  {m.Folder}");
            return Ok(text.ToString().TrimEnd());
        }

        /// <summary>
        /// An encrypted machine's disks for this run of WinQEMU: with the password given, else the one remembered or
        /// already typed. Never the window's password dialog, which WinQEMU hidden in the notification area would
        /// show to nobody: the command asks instead (PasswordRequired).
        /// </summary>
        private async Task UnlockForCommandAsync(VmConfig config, string? password)
        {
            if (!config.DisksEncrypted || Manager.IsUnlocked(config))
                return;
            if (password is null && !Manager.IsDiskPasswordRemembered(config))
                throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.PasswordPrompt, config.Name), CliProtocol.PasswordRequired);
            try
            {
                if (password is not null)
                    await Manager.UnlockWithPasswordAsync(config, password);
                else
                    await Manager.UnlockAsync(config, interactive: false);
            }
            catch (DiskPasswordException ex)
            {
                throw new CommandException(ex.Message);
            }
        }

        private CliResponse Status(string[] args)
        {
            var config = FindMachine(Argument(args, 0, CliText.ArgumentMachine));
            var session = Manager.FindSession(config);
            var text = new StringBuilder();
            text.AppendLine($"{config.Name}: {StateText(StateOf(config))}");
            if (FolderPathOf(config) is { Length: > 0 } folder)
                text.AppendLine(string.Format(CultureInfo.CurrentCulture, CliText.StatusFolder, folder));
            text.AppendLine(string.Format(CultureInfo.CurrentCulture, CliText.StatusLocation, config.FolderPath));
            if (session?.LastError is { Length: > 0 } error)
                text.AppendLine(error);
            return Ok(text.ToString().TrimEnd());
        }

        private async Task<CliResponse> StartFromCommandAsync(string[] args, string? password, CancellationToken cancellationToken)
        {
            var config = FindMachine(Argument(args, 0, CliText.ArgumentMachine));
            if (!Manager.IsQemuAvailable)
                throw new CommandException(CliText.NoQemu);
            var session = Manager.GetSession(config);
            if (session.State != VmState.Stopped && session.State != VmState.Error)
                throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.AlreadyStarted, config.Name, StateText(session.State)));

            await UnlockForCommandAsync(config, password);
            await StartMachineAsync(config);
            if (args.Contains("--wait"))
                await WaitForAsync(session, s => s.State is VmState.Running or VmState.Error or VmState.Stopped, null, cancellationToken);
            if (session.State == VmState.Error)
                throw new CommandException(session.LastError ?? CommonText.Error);
            return Ok(string.Format(CultureInfo.CurrentCulture, CliText.Started, config.Name));
        }

        private async Task<CliResponse> StopFromCommandAsync(string[] args, CancellationToken cancellationToken)
        {
            var config = FindMachine(Argument(args, 0, CliText.ArgumentMachine));
            var session = RunningSessionOf(config);
            // A paused guest would not hear the power button.
            if (session.State == VmState.Paused)
                await session.ResumeAsync();
            await session.RequestShutdownAsync();
            if (!args.Contains("--wait"))
                return Ok(string.Format(CultureInfo.CurrentCulture, CliText.ShutdownRequested, config.Name));

            var seconds = Option(args, "--timeout") is { } value
                ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                    ? parsed
                    : throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.BadTimeout, value), CliProtocol.Misuse)
                : 300;
            if (!await WaitForAsync(session, s => !IsLive(s.State), TimeSpan.FromSeconds(seconds), cancellationToken))
                throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.StillRunning, config.Name, seconds));
            return Ok(string.Format(CultureInfo.CurrentCulture, CliText.Stopped, config.Name));
        }

        private VmSession RunningSessionOf(VmConfig config) =>
            Manager.FindSession(config) is { } session && IsLive(session.State)
                ? session
                : throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.NotRunning, config.Name));

        private async Task<CliResponse> OnRunningAsync(string[] args, Func<VmSession, Task> action, string done)
        {
            var config = FindMachine(Argument(args, 0, CliText.ArgumentMachine));
            await action(RunningSessionOf(config));
            return Ok(string.Format(CultureInfo.CurrentCulture, done, config.Name));
        }

        /// <summary>Polled rather than tied to events: a machine restarted by its session (guest reboot) changes session state many times.</summary>
        private static async Task<bool> WaitForAsync(VmSession session, Func<VmSession, bool> done, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var deadline = timeout is null ? DateTime.MaxValue : DateTime.UtcNow + timeout.Value;
            while (!done(session))
            {
                if (DateTime.UtcNow >= deadline)
                    return false;
                await Task.Delay(500, cancellationToken);
            }
            return true;
        }

        /// <summary>
        /// Through a snapshots view-model of its own, the very one the Snapshots section uses: the same refusals
        /// (machine running, linked clones, partial snapshots) and the same lineage records, without touching what
        /// the window shows.
        /// </summary>
        private async Task<CliResponse> SnapshotFromCommandAsync(string[] args, string? password)
        {
            var action = Argument(args, 0, CliText.ArgumentAction).ToLowerInvariant();
            var config = FindMachine(Argument(args, 1, CliText.ArgumentMachine));
            Exception? failure = null;
            var snapshots = new VmSnapshotsViewModel(Manager, () => Manager.Save(config), ex => failure = ex);
            snapshots.Attach(config);
            snapshots.Session = Manager.FindSession(config) is { IsRunning: true } running ? running : null;
            // Set by the window for its own list; here from the session, as MainViewModel does for the selection.
            snapshots.IsMachineOff = !(Manager.FindSession(config) is { } live && IsLive(live.State));
            if (snapshots.IsMachineOff)
                await UnlockForCommandAsync(config, password);
            await snapshots.RefreshAsync();

            if (action == "list")
            {
                if (snapshots.Snapshots.Count == 0)
                    return Ok(CliText.NoSnapshots);
                return Ok(string.Join(Environment.NewLine, snapshots.Snapshots.Select(s => $"{s.Name}  {s.CreatedText}  {s.DetailText}")));
            }

            var name = Argument(args, 2, CliText.ArgumentSnapshotName);
            if (config.IsTemplate)
                throw new CommandException(string.Format(CultureInfo.CurrentCulture, ShellText.TemplateSnapshotsLocked, config.Name));
            string done;
            switch (action)
            {
                case "create":
                    snapshots.NewName = name;
                    if (snapshots.NameProblem is { } problem)
                        throw new CommandException(problem);
                    if (snapshots.Session is not null)
                    {
                        if (!snapshots.CanTakeLiveSnapshot)
                            throw new CommandException(CliText.SnapshotNotPossible);
                        await snapshots.CreateLiveSnapshotAsync();
                    }
                    else
                    {
                        if (!snapshots.CanCreate)
                            throw new CommandException(CliText.SnapshotNotPossible);
                        await snapshots.CreateAsync();
                    }
                    done = CliText.SnapshotCreated;
                    break;
                case "restore":
                case "delete":
                    var item = snapshots.Snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
                               ?? throw new CommandException(string.Format(CultureInfo.CurrentCulture, CliText.UnknownSnapshot, name, config.Name));
                    snapshots.Selected = item;
                    if (!snapshots.CanActOnSelection)
                        throw new CommandException(snapshots.IsLockedByRunningMachine ? CliText.SnapshotNeedsStoppedMachine : CliText.SnapshotNotPossible);
                    if (action == "restore")
                    {
                        await snapshots.RestoreAsync(item);
                        done = CliText.SnapshotRestored;
                    }
                    else
                    {
                        if (!snapshots.CanDeleteSelection)
                            throw new CommandException(item.LockReason ?? CliText.SnapshotNotPossible);
                        await snapshots.DeleteAsync(item);
                        done = CliText.SnapshotDeleted;
                    }
                    break;
                default:
                    return Misuse(string.Format(CultureInfo.CurrentCulture, CliText.UnknownSnapshotAction, action));
            }

            if (failure is not null)
                throw new CommandException(failure.Message);
            // The window's own list, if it shows this machine, catches up.
            if (ReferenceEquals(SelectedVm, config))
                await Snapshots.RefreshAsync();
            return Ok(string.Format(CultureInfo.CurrentCulture, done, name, config.Name));
        }

        private CliResponse Show(string[] args)
        {
            // Not SelectConfig: it files a machine of the root into the open folder, as it does for a new one.
            if (args.Length > 0 && FindMachine(args[0]) is var config)
                SelectedItem = _allItems.FirstOrDefault(item => ReferenceEquals(item.Config, config));
            ShowRequested?.Invoke();
            return Ok(string.Empty);
        }
    }
}
