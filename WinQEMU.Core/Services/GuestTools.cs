using System.Text;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

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
        /// <summary>
        /// The agent answers, but its distribution holds it back (guest-exec blocked, or what it runs confined by
        /// SELinux): nothing can be fixed through it, and the command typed in the guest lifts both.
        /// </summary>
        Restricted,
        /// <summary>The agent answers, but the rest of the tools are not there (clipboard, display drivers).</summary>
        Incomplete,
        Complete
    }

    /// <param name="Missing">What is missing, in English, for the machine's log; null when nothing is.</param>
    public sealed record GuestToolsReport(GuestToolsState State, string Text, string? Missing = null);

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
        /// the distribution has, the agent freed of what its distribution holds it back with
        /// (<see cref="LinuxReleaseAgent"/>), then started again (its unit waits for the virtio port, which is there).
        /// </summary>
        public const string LinuxInstallCommand =
            "sudo sh -c 'if command -v apt-get >/dev/null; then apt-get install -y qemu-guest-agent spice-vdagent; " +
            "elif command -v dnf >/dev/null; then dnf install -y qemu-guest-agent spice-vdagent " + FedoraPerl + "; " +
            "elif command -v zypper >/dev/null; then zypper -n in qemu-guest-agent spice-vdagent; " +
            "elif command -v pacman >/dev/null; then pacman -S --noconfirm qemu-guest-agent spice-vdagent; fi; " +
            LinuxReleaseAgent + "'";

        /// <summary>
        /// What an unattended installation by WinQEMU does to the agent (LinuxUnattendedSetup.UnconfinedAgent and
        /// OpenSuseAgentCommands), for a guest installed by hand: the agent's SELinux domain made permissive (Fedora,
        /// openSUSE: what it ran could not list /home nor load 9pnet_fd), and openSUSE's unit without its
        /// "--block-rpcs=guest-exec,…" ("Command guest-exec has been disabled"). Without them shared folders, file
        /// copies and drags failed on such a guest with the agent answering, and nothing said why.
        /// </summary>
        /// <remarks>
        /// Typed, not run by the agent: a blocked guest-exec runs nothing, and a confined one may not load a policy
        /// module. Inside the single quotes of "sh -c": double quotes only. The agent is (re)started first and its
        /// command line read: openSUSE's unit says only "${FILTER_RPC_ARGS}", set in /usr/etc/sysconfig/qemu-ga, so the
        /// unit's text does not show the block. The unit is overridden only where guest-exec is among the blocked
        /// commands, so another distribution's own choices are left alone.
        /// </remarks>
        public const string LinuxReleaseAgent =
            "if command -v semodule >/dev/null; then echo \"(typepermissive virt_qemu_ga_t)\" >/tmp/winqemu_qemu_ga.cil; " +
            "semodule -i /tmp/winqemu_qemu_ga.cil; rm -f /tmp/winqemu_qemu_ga.cil; fi; " +
            "systemctl restart qemu-guest-agent; sleep 2; " +
            "if ps -C qemu-ga -o args= | grep -q \"block-rpcs.*guest-exec\"; then " +
            "d=/etc/systemd/system/qemu-guest-agent.service.d; mkdir -p $d; " +
            "printf \"[Service]\\nExecStart=\\nExecStart=/usr/bin/qemu-ga -p /dev/virtio-ports/org.qemu.guest_agent.0\\n\" >$d/winqemu.conf; " +
            "systemctl daemon-reload; systemctl restart qemu-guest-agent; fi";

        /// <summary>
        /// Perl as WinQEMU's guest programs need it, in Fedora's packages: the interpreter and the core modules they use
        /// (IO::Handle, Socket, %! for Errno), which Fedora ships apart.
        /// </summary>
        /// <remarks>
        /// The file drop's receiver, the pull's sender and the shared folders' pump are Perl, there on Debian and Ubuntu
        /// (perl-base is essential) and on openSUSE, but not on a Fedora installed with Xfce: a drag out of the guest
        /// read its list, then failed on "Failed to execute child process "perl" (No such file or directory)".
        /// </remarks>
        public const string FedoraPerl = "perl-interpreter perl-IO perl-Socket perl-Errno";

        /// <summary>
        /// Run by the agent as root: what is missing, without a question. The clipboard agent for a guest with a desktop
        /// only, and Perl (see <see cref="FedoraPerl"/>). Exit 31: no known package manager.
        /// </summary>
        public static readonly string LinuxToolsScript = """
            export DEBIAN_FRONTEND=noninteractive
            packages=
            desktop=
            for f in /usr/share/xsessions/*.desktop /usr/share/wayland-sessions/*.desktop; do [ -e "$f" ] && desktop=1; done
            if [ -n "$desktop" ] && ! command -v spice-vdagent >/dev/null; then packages=spice-vdagent; fi
            if command -v apt-get >/dev/null; then apt-get install -y $packages perl-base
            elif command -v dnf >/dev/null; then dnf install -y $packages {FEDORA_PERL}
            elif command -v zypper >/dev/null; then zypper -n in $packages perl
            elif command -v pacman >/dev/null; then pacman -S --noconfirm --needed $packages perl
            else exit 31; fi
            """.Replace("{FEDORA_PERL}", FedoraPerl, StringComparison.Ordinal).ReplaceLineEndings("\n");

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
        /// "systemctl get-default" said graphical.target on that Debian too. And whether Perl is there with the modules
        /// WinQEMU's guest programs use (see <see cref="FedoraPerl"/>).
        /// </summary>
        public const string LinuxCheckScript =
            "command -v spice-vdagent >/dev/null && echo present; " +
            // One by one: "ls a b" fails as soon as one of the two folders is missing, and a guest with X11 sessions only
            // (Fedora with Xfce) or Wayland ones only (Ubuntu) was said to have no desktop.
            "desktop=; for f in /usr/share/xsessions/*.desktop /usr/share/wayland-sessions/*.desktop; do [ -e \"$f\" ] && desktop=1; done; " +
            "[ -n \"$desktop\" ] || echo headless; " +
            "perl -MIO::Handle -MSocket -e 'my $e = $!{EBUSY}' 2>/dev/null && echo perl; " +
            // What SELinux denies a confined agent's commands (Fedora or openSUSE installed by hand), and what file copies
            // need: writing in a user's folder. Not "ls /home", which Fedora 45 allows (so does listing the folder; reading
            // a file in it is denied), nor /sys/fs/selinux/enforce, which a confined agent may not even see.
            "if [ -d /sys/fs/selinux ]; then for d in /home/*/; do [ -d \"$d\" ] || continue; " +
            "t=$(mktemp -p \"$d\" .winqemu-XXXXXX 2>/dev/null) && rm -f \"$t\" || echo confined; break; done; fi";

        /// <summary>
        /// Whether the agent refused to run anything: openSUSE starts it with guest-exec blocked, and QEMU's agent
        /// answers "Command guest-exec has been disabled".
        /// </summary>
        public static bool IsExecDisabled(string agentError) =>
            agentError.Contains("guest-exec", StringComparison.OrdinalIgnoreCase)
            && agentError.Contains("disabled", StringComparison.OrdinalIgnoreCase);

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
                try
                {
                    result = await agent.RunAsync("/bin/sh", new[] { "-c", LinuxCheckScript },
                        null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch (GuestAgentException ex) when (IsExecDisabled(ex.Message))
                {
                    // Before: "could not be checked", state unknown, and no way offered to fix it.
                    return new GuestToolsReport(GuestToolsState.Restricted, CoreText.GuestToolsRestricted, "guest-exec is blocked");
                }
            }

            return Report(result.Output, windowsGuest);
        }

        /// <summary>What the check's output means (<see cref="LinuxCheckScript"/>, or the Windows service found).</summary>
        public static GuestToolsReport Report(string output, bool windowsGuest)
        {
            bool Has(string word) => output.Contains(word, StringComparison.Ordinal);
            // First: whatever else is there, nothing WinQEMU runs in this guest reaches the user's files.
            if (!windowsGuest && Has("confined"))
                return new GuestToolsReport(GuestToolsState.Restricted, CoreText.GuestToolsRestricted, "SELinux confines the agent");
            // Before the clipboard: without Perl nothing is copied and no shared folder is mounted, desktop or not.
            if (!windowsGuest && !Has("perl"))
                return new GuestToolsReport(GuestToolsState.Incomplete, CoreText.GuestToolsNoPerl, "Perl");
            if (Has("present"))
                return new GuestToolsReport(GuestToolsState.Complete, CoreText.GuestToolsInstalled);
            if (Has("headless"))
                return new GuestToolsReport(GuestToolsState.Complete, CoreText.GuestToolsHeadless);
            return windowsGuest
                ? new GuestToolsReport(GuestToolsState.Incomplete, CoreText.GuestToolsIncompleteWindows, "virtio-win's guest tools")
                : new GuestToolsReport(GuestToolsState.Incomplete, CoreText.GuestToolsIncompleteLinux, "spice-vdagent");
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
            30 => CoreText.GuestToolsNoVirtioDrive,
            31 => CoreText.GuestToolsNoPackageManager,
            3010 => CoreText.GuestToolsRestartNeeded,
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
