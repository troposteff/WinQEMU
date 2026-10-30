using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>
        /// Restarts the guest cleanly, as its own Restart would: programs closed, files saved. Through the guest agent
        /// (guest-shutdown, mode reboot) when it answers; without one, nothing in QEMU asks a guest to reboot, so the ACPI
        /// power button, then a start once the guest is off. A UEFI guest's reboot is a cold restart of QEMU, as always
        /// (VmSession, guest-reset). Returns what to tell the user, or null.
        /// </summary>
        public async Task<string?> RestartAsync()
        {
            if (SelectedVm is not { } config || SelectedSession is not { IsRunning: true } session)
                return ShellText.MachineMustRun;
            if (session.State == VmState.Paused)
                await session.ResumeAsync();

            await using (var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromSeconds(5)))
            {
                if (agent is not null)
                {
                    try
                    {
                        await agent.ExecuteAsync("guest-shutdown", new JsonObject { ["mode"] = "reboot" }, TimeSpan.FromSeconds(5));
                        AppendDiagnostic("Restart: the guest agent is rebooting the guest.");
                        return null;
                    }
                    catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
                    {
                        // The agent goes down with the guest: no answer is the expected one.
                        AppendDiagnostic("Restart: the guest agent is rebooting the guest.");
                        return null;
                    }
                    catch (GuestAgentException ex)
                    {
                        AppendDiagnostic($"Restart: the guest agent refused to reboot ({ex.Message}); power button instead.");
                    }
                }
            }

            AppendDiagnostic("Restart: power button, then a start once the guest is off.");
            await session.RequestShutdownAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (IsLive(session.State))
            {
                if (DateTime.UtcNow > deadline)
                    return ShellText.RestartNoShutdown;
                await Task.Delay(500);
            }
            await StartMachineAsync(config);
            return null;
        }
    }
}
