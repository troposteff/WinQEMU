using System.Text;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Services
{
    public enum GuestToolsState
    {
        /// <summary>Not looked at yet, or the machine is not running.</summary>
        Unknown,
        /// <summary>Waiting for the guest agent, which may still be booting.</summary>
        Checking,
        /// <summary>No agent answered: nothing works that goes through the guest.</summary>
        AgentMissing,
        /// <summary>The agent answers, but the rest of the tools are not there (clipboard, display drivers).</summary>
        Incomplete,
        Complete
    }

    public sealed record GuestToolsReport(GuestToolsState State, string Text);

    /// <summary>
    /// The guest's side of WinQEMU, as VMware Tools are VMware's: qemu-guest-agent, on which shared folders,
    /// file drops, the display size and the network chart depend, and spice-vdagent for the clipboard; on
    /// Windows, virtio-win's guest tools (drivers, spice agent) and the agent, a separate installer on the same
    /// ISO. Found missing, they are installed in a click (see the shell's install flow).
    /// </summary>
    /// <remarks>
    /// Without an agent nothing can be run in the guest. On Linux the first step is typed at its keyboard
    /// (Input.KeyboardText): the package manager's command in a terminal the user opened, with sudo's password.
    /// On Windows, WinQEMU puts virtio-win's ISO in the drive and the user starts its two installers (the guest
    /// tools, then guest-agent\qemu-ga-x86_64.msi); the tools must come first, as the agent talks through the
    /// virtio-serial driver they bring (uninstalling the tools silenced an agent that had worked).
    /// Typing that in the Run box was tried and dropped: a PowerShell command typed there, started elevated, is
    /// exactly the "ClickFix" attack, and Microsoft Defender stopped it (Behavior:Win32/SuspClickFix, severity 5)
    /// and killed the virtio installer it had started. A protection is not to be worked around.
    /// Everything else goes through the agent, as SYSTEM or root, without a question: on Windows, Red Hat's
    /// publisher is trusted first, or each virtio driver would ask "Installer ce logiciel de périphérique ?".
    /// </remarks>
    public static class GuestTools
    {
        /// <summary>How long an agent may take to answer after a start before it is said missing.</summary>
        public static readonly TimeSpan AgentWait = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Run by the agent as SYSTEM: trusts Red Hat's publisher (from a driver catalog's signature, as the
        /// unattended installation does), then installs virtio-win's guest tools without a question. Exit 30:
        /// no virtio-win drive.
        /// </summary>
        public const string WindowsToolsScript = """
            $ProgressPreference = 'SilentlyContinue'
            $drive = Get-PSDrive -PSProvider FileSystem | Where-Object { Test-Path (Join-Path $_.Root 'virtio-win-guest-tools.exe') } | Select-Object -First 1
            if (-not $drive) { exit 30 }
            $root = $drive.Root
            $catalog = Get-ChildItem (Join-Path $root 'Balloon') -Recurse -Filter *.cat -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($catalog) {
              $certificate = (Get-AuthenticodeSignature $catalog.FullName).SignerCertificate
              if ($certificate) {
                $store = New-Object Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'LocalMachine')
                $store.Open('ReadWrite'); $store.Add($certificate); $store.Close()
              }
            }
            $process = Start-Process (Join-Path $root 'virtio-win-guest-tools.exe') -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
            exit $process.ExitCode
            """;

        /// <summary>
        /// Typed in a terminal of the guest: the agent and the clipboard agent from whichever package manager
        /// the distribution has, then the agent started (its unit waits for the virtio port, which is there).
        /// </summary>
        public const string LinuxInstallCommand =
            "sudo sh -c 'if command -v apt-get >/dev/null; then apt-get install -y qemu-guest-agent spice-vdagent; " +
            "elif command -v dnf >/dev/null; then dnf install -y qemu-guest-agent spice-vdagent; " +
            "elif command -v zypper >/dev/null; then zypper -n in qemu-guest-agent spice-vdagent; " +
            "elif command -v pacman >/dev/null; then pacman -S --noconfirm qemu-guest-agent spice-vdagent; fi; " +
            "systemctl start qemu-guest-agent'";

        /// <summary>Run by the agent as root: the clipboard agent, without a question. Exit 31: no known package manager.</summary>
        public const string LinuxToolsScript = """
            export DEBIAN_FRONTEND=noninteractive
            if command -v apt-get >/dev/null; then apt-get install -y spice-vdagent
            elif command -v dnf >/dev/null; then dnf install -y spice-vdagent
            elif command -v zypper >/dev/null; then zypper -n in spice-vdagent
            elif command -v pacman >/dev/null; then pacman -S --noconfirm spice-vdagent
            else exit 31; fi
            """;

        /// <summary>Whether the machine's profile is one WinQEMU has tools for.</summary>
        public static bool Applies(VmConfig config) =>
            GuestOsCatalog.Find(config.GuestOs)?.Family is "Windows" or "Linux";

        /// <summary>
        /// Whether a machine runs its installed system: during an installation (unattended or from an ISO) no
        /// agent can be there yet, and saying it is missing would only alarm.
        /// </summary>
        public static bool ShouldCheck(VmConfig config) =>
            Applies(config) && !config.BootFromIso && config.UnattendedSetup == UnattendedSetupState.None;

        public static bool IsWindows(VmConfig config) => GuestOsCatalog.Find(config.GuestOs)?.Family == "Windows";

        /// <summary>
        /// Asked of a Linux guest: whether spice-vdagent is there, and whether it has a desktop at all. Without
        /// one (a server, like the user's Debian), the clipboard agent has nothing to do, and calling the tools
        /// incomplete only nagged. A desktop is known by the sessions it declares to its display manager;
        /// "systemctl get-default" said graphical.target on that Debian too.
        /// </summary>
        public const string LinuxCheckScript =
            "command -v spice-vdagent >/dev/null && echo present; " +
            "ls /usr/share/xsessions/*.desktop /usr/share/wayland-sessions/*.desktop >/dev/null 2>&1 || echo headless";

        /// <summary>What the guest has, asked of its agent.</summary>
        public static async Task<GuestToolsReport> CheckAsync(GuestAgentClient agent, bool windowsGuest, CancellationToken cancellationToken = default)
        {
            GuestExecResult result;
            if (windowsGuest)
            {
                // spice-agent is the service virtio-win's guest tools install (seen on Windows 11 with 0.1.262).
                var script = "if (Get-Service spice-agent -ErrorAction SilentlyContinue) { 'present' }";
                result = await agent.RunAsync("powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
                    null, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await agent.RunAsync("/bin/sh", new[] { "-c", LinuxCheckScript },
                    null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }

            if (result.Output.Contains("present", StringComparison.Ordinal))
                return new GuestToolsReport(GuestToolsState.Complete, "Outils invités installés.");
            if (result.Output.Contains("headless", StringComparison.Ordinal))
            {
                return new GuestToolsReport(GuestToolsState.Complete,
                    "L'agent invité répond. Sans interface graphique, l'invité n'a pas de presse-papiers à partager.");
            }
            return new GuestToolsReport(GuestToolsState.Incomplete, windowsGuest
                ? "L'agent invité répond, mais les outils virtio-win (pilotes, presse-papiers) ne sont pas installés."
                : "L'agent invité répond, mais spice-vdagent n'est pas installé : le presse-papiers n'est pas partagé.");
        }

        /// <summary>Installs what the agent alone does not bring. Windows needs virtio-win's ISO in a drive.</summary>
        public static async Task<GuestExecResult> CompleteAsync(GuestAgentClient agent, bool windowsGuest, CancellationToken cancellationToken = default)
        {
            return windowsGuest
                ? await agent.RunAsync("powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsToolsScript)) },
                    null, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false)
                : await agent.RunAsync("/bin/sh", new[] { "-c", LinuxToolsScript }, null, TimeSpan.FromMinutes(10), cancellationToken)
                    .ConfigureAwait(false);
        }

        public static string Explain(GuestExecResult result) => result.ExitCode switch
        {
            30 => "aucun lecteur ne contient l'ISO virtio-win",
            31 => "la distribution n'a ni apt, ni dnf, ni zypper, ni pacman",
            3010 => "installé ; un redémarrage de l'invité termine l'installation des pilotes",
            _ => $"code {result.ExitCode} {result.Error.Trim()}".Trim()
        };

        /// <summary>
        /// virtio-win's ISO, without downloading anything: the one chosen before, else one a machine of the
        /// library uses (by its file name), else the newest in the user's Downloads folder. Null: the user is
        /// asked for it.
        /// </summary>
        public static string? FindVirtioWinIso(string? remembered, IEnumerable<VmConfig> library, string? downloads = null)
        {
            if (!string.IsNullOrWhiteSpace(remembered) && File.Exists(remembered))
                return remembered;

            foreach (var config in library)
            {
                foreach (var iso in new[] { config.IsoPath, config.SecondaryIsoPath })
                {
                    if (string.IsNullOrWhiteSpace(iso))
                        continue;
                    var path = config.ResolvePath(iso);
                    if (IsVirtioWinIso(path) && File.Exists(path))
                        return path;
                }
            }

            downloads ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads))
                return null;
            return new DirectoryInfo(downloads).EnumerateFiles("virtio-win*.iso")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        public static bool IsVirtioWinIso(string path) =>
            Path.GetFileName(path).StartsWith("virtio-win", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
    }
}
