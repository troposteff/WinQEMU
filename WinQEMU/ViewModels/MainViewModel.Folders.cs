using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// A folder's machines run as one, as VMware's teams do: started one after the other with the delays of their automatic
    /// start (AutoStartPlan.ForGroup), shut down, paused or resumed together. The machines of its subfolders count too
    /// (LibraryTree.MachinesIn). From the folder's summary, its menu in the list, and the winqemu command.
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>The folder being started, if any: one at a time, and it can be stopped between two machines.</summary>
        private (string FolderId, CancellationTokenSource Cancel)? _folderStart;

        private string _folderStartStatus = string.Empty;
        public string FolderStartStatus { get => _folderStartStatus; private set => SetProperty(ref _folderStartStatus, value); }

        public bool IsStartingFolder => _folderStart is not null;

        public IReadOnlyList<VmConfig> MachinesOf(string folderId) => Library.MachinesIn(folderId, Manager.VirtualMachines);

        private IReadOnlyList<VmConfig> SelectedFolderMachines =>
            SelectedFolder is { } folder ? MachinesOf(folder.Id) : Array.Empty<VmConfig>();

        public bool CanStartFolder =>
            _folderStart is null && Manager.IsQemuAvailable
            && SelectedFolderMachines.Any(m => !m.IsTemplate && StateOf(m) is VmState.Stopped or VmState.Error);

        public bool CanStopFolder => SelectedFolderMachines.Any(m => StateOf(m) is VmState.Running or VmState.Paused);

        public bool CanPauseFolder => SelectedFolderMachines.Any(m => StateOf(m) == VmState.Running);

        public bool CanResumeFolder => SelectedFolderMachines.Any(m => StateOf(m) == VmState.Paused);

        private void RaiseFolderActions()
        {
            RaisePropertyChanged(nameof(CanStartFolder));
            RaisePropertyChanged(nameof(CanStopFolder));
            RaisePropertyChanged(nameof(CanPauseFolder));
            RaisePropertyChanged(nameof(CanResumeFolder));
            RaisePropertyChanged(nameof(IsStartingFolder));
        }

        /// <summary>The outcome of a folder command, for the command line: what was done, and what was left and why.</summary>
        public sealed record FolderOutcome(int Done, IReadOnlyList<string> Skipped);

        /// <summary>
        /// Starts the folder's stopped machines, each after its delay from now, the shortest first. A machine started, or
        /// removed, in the meantime is left; one that fails is said and the next ones still start. An encrypted machine
        /// asks for its password as Démarrer does — or, from the command line (<paramref name="prepare"/>), is left when
        /// none is remembered.
        /// </summary>
        public async Task<FolderOutcome> StartFolderAsync(string folderId, Func<VmConfig, Task<string?>>? prepare = null)
        {
            if (_folderStart is not null)
                throw new InvalidOperationException(ShellText.FolderStartBusy);

            var plan = AutoStartPlan.ForGroup(MachinesOf(folderId));
            var cancel = new CancellationTokenSource();
            _folderStart = (folderId, cancel);
            RaiseFolderActions();
            var done = 0;
            var skipped = new List<string>();
            var clicked = DateTime.UtcNow;
            try
            {
                for (var index = 0; index < plan.Count; index++)
                {
                    var (machine, after) = plan[index];
                    var wait = clicked + after - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        FolderStartStatus = string.Format(CultureInfo.CurrentCulture, ShellText.FolderStartWaiting,
                            machine.Name, DateTime.Now + wait);
                        await Task.Delay(wait, cancel.Token);
                    }
                    if (!Manager.VirtualMachines.Contains(machine))
                        continue;
                    var session = Manager.GetSession(machine);
                    if (session.State is not (VmState.Stopped or VmState.Error))
                        continue;

                    FolderStartStatus = string.Format(CultureInfo.CurrentCulture, ShellText.FolderStartProgress, machine.Name, index + 1, plan.Count);
                    try
                    {
                        if (prepare is not null && await prepare(machine) is { } reason)
                        {
                            skipped.Add($"{machine.Name} : {reason}");
                            continue;
                        }
                        AppendDiagnostic($"Folder start: starting {machine.Name}.");
                        await StartMachineAsync(session);
                        done++;
                    }
                    // InvalidOperationException covers DiskPasswordException (a wrong or missing password).
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or QmpException or TimeoutException
                                                   or QemuImgException or System.ComponentModel.Win32Exception)
                    {
                        skipped.Add($"{machine.Name} : {ex.Message}");
                        ReportError(new InvalidOperationException(
                            string.Format(CultureInfo.CurrentCulture, ShellText.FolderStartFailed, machine.Name, ex.Message), ex));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped between two machines: those already started keep running.
            }
            finally
            {
                _folderStart = null;
                cancel.Dispose();
                FolderStartStatus = string.Empty;
                RaiseFolderActions();
            }
            return new FolderOutcome(done, skipped);
        }

        /// <summary>Stops starting the folder: the machines not started yet stay off.</summary>
        public void CancelFolderStart() => _folderStart?.Cancel.Cancel();

        /// <summary>
        /// Asks each running machine of the folder to shut down (the power button), in the reverse order of a start: the
        /// clients before their server. A paused one is resumed first, or it would not hear the button.
        /// </summary>
        public Task<FolderOutcome> StopFolderAsync(string folderId) =>
            OnFolderAsync(folderId, reverse: true, s => s.State is VmState.Running or VmState.Paused, async session =>
            {
                if (session.State == VmState.Paused)
                    await session.ResumeAsync();
                await session.RequestShutdownAsync();
            });

        public Task<FolderOutcome> PauseFolderAsync(string folderId) =>
            OnFolderAsync(folderId, reverse: false, s => s.State == VmState.Running, s => s.PauseAsync());

        public Task<FolderOutcome> ResumeFolderAsync(string folderId) =>
            OnFolderAsync(folderId, reverse: false, s => s.State == VmState.Paused, s => s.ResumeAsync());

        private async Task<FolderOutcome> OnFolderAsync(string folderId, bool reverse, Func<VmSession, bool> applies, Func<VmSession, Task> action)
        {
            var plan = AutoStartPlan.ForGroup(MachinesOf(folderId)).Select(e => e.Machine).ToList();
            if (reverse)
                plan.Reverse();
            var done = 0;
            var skipped = new List<string>();
            foreach (var machine in plan)
            {
                if (Manager.FindSession(machine) is not { } session || !applies(session))
                    continue;
                try
                {
                    await action(session);
                    done++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or QmpException or IOException)
                {
                    skipped.Add($"{machine.Name} : {ex.Message}");
                    AppendDiagnostic($"{machine.Name}: {ex.Message}");
                }
            }
            RaiseFolderActions();
            return new FolderOutcome(done, skipped);
        }
    }
}
