using System.Globalization;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>Where shared folders stand in one running guest.</summary>
    public enum GuestShareState
    {
        WaitingForAgent,
        NoAgent,
        NotLinux,
        Mounted,
        Failed
    }

    /// <param name="Folder">For a Linux guest whose folders were mounted: the folder under its user's home that holds them.</param>
    public sealed record GuestShareReport(GuestShareState State, string Text, string? Folder = null);

    /// <summary>
    /// Mounts the shared folders' private channel inside a Linux guest, through qemu-guest-agent, so they
    /// are simply there in ~/Shares (~/Partages in French), as VMware's are in /mnt/hgfs.
    /// </summary>
    /// <remarks>
    /// The channel is a virtio-serial port served by Sharing.NinePServer; the guest kernel's own 9p client
    /// mounts it through a small relay (see <see cref="MountScript"/>), with the Unix socket transport of
    /// 9pnet_fd, a module in Ubuntu and the other distributions.
    /// No network, no account, no password: an earlier version shared through Windows' file server, and
    /// Windows refused the user's Entra ID account coming from the guest (NTLM events 4023, AzureAD\…).
    ///
    /// Mounted once; folders shared later appear under it without anything done in the guest.
    /// </remarks>
    public static class GuestShareMounter
    {
        /// <summary>
        /// Folder, under the guest user's home, that shared folders appear in when a guest is first set up:
        /// it follows the interface's language. A guest keeps the one it got (see <see cref="MountScript"/>).
        /// </summary>
        public static string GuestFolder => UiLanguage.IsFrench ? FrenchGuestFolder : EnglishGuestFolder;

        private const string FrenchGuestFolder = "Partages";
        private const string EnglishGuestFolder = "Shares";

        /// <summary>
        /// Run as root by the agent. Prints the mount point; exit codes 10-15 are explained by <see cref="Explain"/>.
        /// </summary>
        /// <remarks>
        /// The folder is named in the interface's language the first time, then kept: its name is noted in
        /// /var/lib/winqemu/shares-folder, so a guest set up in French keeps ~/Partages after WinQEMU is switched
        /// to English, and the other way round. A guest set up before that note existed (always ~/Partages) is
        /// recognised by the folder itself: when only the other language's folder exists, it is the one.
        ///
        /// The kernel cannot use the virtio port itself: its driver only takes writes from user space
        /// ("kernel write not supported for file /vport2p2", and mount fails with "bad option"). So a relay
        /// opens the port — once, as a port accepts a single opener — and listens on a Unix socket, which
        /// the kernel connects to (trans=unix). The relay runs detached (setsid) and outlives the agent's
        /// command.
        ///
        /// A socket, not a pair of named pipes with trans=fd: the 9p fd transport keeps only two wait-queue
        /// slots (MAXPOLLWADDR) and a pipe takes both, so the second pipe's "writable again" never reached
        /// it. Small messages went through; a 256 KiB write filled the pipe and hung for good in
        /// p9_client_rpc. A socket is one object with one queue.
        ///
        /// The relay is Perl (perl-base is essential on Debian and Ubuntu), Python as a fallback.
        /// </remarks>
        public static string MountScript => MountScriptTemplate
            .Replace("{FOLDER}", GuestFolder, StringComparison.Ordinal)
            .Replace("{OTHER}", GuestFolder == FrenchGuestFolder ? EnglishGuestFolder : FrenchGuestFolder, StringComparison.Ordinal)
            // A checkout with Windows line endings puts them in the literal: /bin/sh then failed on "set -u\r"
            // with "set: Illegal option -", and no folder was mounted.
            .ReplaceLineEndings("\n");

        private const string MountScriptTemplate = """
            set -u
            user=$(awk -F: '$3 >= 1000 && $3 < 60000 && $7 !~ /(nologin|false)$/ { print $1; exit }' /etc/passwd)
            [ -n "$user" ] || exit 10
            home=$(getent passwd "$user" | cut -d: -f6); uid=$(id -u "$user"); gid=$(id -g "$user")
            state=/var/lib/winqemu/shares-folder
            name=$(cat "$state" 2>/dev/null)
            case "$name" in
              Partages|Shares) ;;
              *) if [ ! -d "$home/{FOLDER}" ] && [ -d "$home/{OTHER}" ]; then name='{OTHER}'; else name='{FOLDER}'; fi ;;
            esac
            mkdir -p /var/lib/winqemu 2>/dev/null && echo "$name" > "$state" 2>/dev/null
            dir="$home/$name"
            if mountpoint -q "$dir"; then echo "$dir"; exit 0; fi
            port=/dev/virtio-ports/org.winqemu.shares.0
            [ -e "$port" ] || exit 13
            modprobe 9pnet_fd 2>/dev/null; modprobe 9p 2>/dev/null
            mkdir -p "$dir" && chown "$uid:$gid" "$dir"
            run=/run/winqemu; sock="$run/shares.sock"
            mkdir -p "$run" && chmod 700 "$run" && rm -f "$sock" || exit 14
            if command -v perl >/dev/null 2>&1; then
              setsid perl -MSocket -e '
                open(my $p, "+<", $ARGV[0]) or exit 1;
                socket(my $l, AF_UNIX, SOCK_STREAM, 0) or exit 1; bind($l, pack_sockaddr_un($ARGV[1])) or exit 1;
                listen($l, 1) or exit 1; accept(my $c, $l) or exit 1; close $l;
                sub pump { my ($r, $w) = @_; my $b; while (my $n = sysread($r, $b, 65536)) { my $o = 0;
                  while ($o < $n) { my $k = syswrite($w, $b, $n - $o, $o); exit 1 unless defined $k; $o += $k } } exit 0 }
                if (fork) { pump($c, $p) } else { pump($p, $c) }' "$port" "$sock" </dev/null >/dev/null 2>&1 &
            elif command -v python3 >/dev/null 2>&1; then
              setsid python3 -c '
            import os, socket, sys
            p = os.open(sys.argv[1], os.O_RDWR)
            l = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); l.bind(sys.argv[2]); l.listen(1)
            c = l.accept()[0].detach(); l.close()
            def pump(r, w):
                while True:
                    b = os.read(r, 65536)
                    if not b: os._exit(0)
                    while b: b = b[os.write(w, b):]
            pump(c, p) if os.fork() else pump(p, c)' "$port" "$sock" </dev/null >/dev/null 2>&1 &
            else
              exit 15
            fi
            for i in 1 2 3 4 5 6 7 8 9 10; do [ -S "$sock" ] && break; sleep 0.5; done
            [ -S "$sock" ] || exit 14
            mount -t 9p -o "trans=unix,version=9p2000.L,msize=262144,aname=$uid:$gid" "$sock" "$dir" || exit 12
            echo "$dir"
            """;

        /// <summary>
        /// The drive's name in the guest's Explorer when a guest is first set up, in the interface's language.
        /// A guest keeps the one it got (see <see cref="WindowsSetupScript"/>).
        /// </summary>
        public static string WindowsDriveLabel => UiLanguage.IsFrench ? FrenchDriveLabel : "WinQEMU Shares";

        private const string FrenchDriveLabel = "Partages WinQEMU";

        /// <summary>
        /// Run in each user's own session by a logon task (see <see cref="WindowsSetupScript"/>): maps the
        /// shares to a free drive letter, from Z: down, as VMware maps its own. A mapping made by the agent
        /// would be SYSTEM's alone, invisible to the user.
        /// </summary>
        /// <remarks>
        /// The address changes with every run of QEMU (a new port and secret), so it is read from the
        /// registry, where the agent left it, and a mapping to an earlier address is removed first.
        /// 10.0.2.2 is put in the Local intranet zone, or Windows treats files opened from it as downloaded
        /// from the Internet (security warnings, Office's protected view). The drive's name is read there too.
        /// </remarks>
        public const string WindowsMapScript = """
            $ProgressPreference = 'SilentlyContinue'
            $saved = Get-ItemProperty 'HKLM:\SOFTWARE\WinQEMU' -ErrorAction SilentlyContinue
            $url = $saved.SharesUrl
            if (-not $url) { exit 0 }
            $name = $saved.SharesLabel
            if (-not $name) { $name = 'Partages WinQEMU' }
            $zone = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\WinQEMU'
            New-Item $zone -Force | Out-Null
            Set-ItemProperty $zone ':Range' '10.0.2.2'
            Set-ItemProperty $zone 'file' 1 -Type DWord
            Set-ItemProperty $zone 'http' 1 -Type DWord
            $drives = Get-PSDrive -PSProvider FileSystem
            if ($drives | Where-Object { $_.DisplayRoot -eq $url }) { exit 0 }
            foreach ($old in $drives | Where-Object { $_.DisplayRoot -like '\\10.0.2.2@*' }) { net use "$($old.Name):" /delete /y | Out-Null }
            $letter = 'ZYXWVUTSRQPONMLKJIHGFE'.ToCharArray() | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
            if (-not $letter) { exit 1 }
            net use "$($letter):" $url /persistent:no | Out-Null
            $label = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\' + ($url -replace '\\', '#')
            New-Item $label -Force | Out-Null
            Set-ItemProperty $label '_LabelFromReg' $name
            """;

        /// <summary>
        /// Run as SYSTEM by the agent in a Windows guest, with {URL} the shares' address for this run. Prints
        /// the address, then the drive's name; exit codes 20-21 are explained by <see cref="Explain"/>.
        /// </summary>
        /// <remarks>
        /// The drive's name ({LABEL}, in the interface's language) is noted in HKLM\SOFTWARE\WinQEMU the first
        /// time and kept from then on, so a guest set up in French keeps « Partages WinQEMU » after WinQEMU is
        /// switched to English. A guest set up before that note existed has the address but no name: it had
        /// « Partages WinQEMU ». Both are read before New-Item -Force, which recreates the key empty.
        ///
        /// Windows' WebDAV client (the WebClient service) is set to start with Windows and to take files up
        /// to 4 GB (its default stops at 50 MB). The drive itself is mapped by a logon task of the Users group
        /// (S-1-5-32-545: group names are translated, "Utilisateurs" here), started at once for whoever is
        /// logged on; conhost --headless keeps PowerShell's window from flashing up. Progress is silenced:
        /// PowerShell writes it to standard error as CLIXML, which would read as a failure in the journal.
        /// </remarks>
        public const string WindowsSetupScript = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $url = '{URL}'
            $saved = Get-ItemProperty 'HKLM:\SOFTWARE\WinQEMU' -ErrorAction SilentlyContinue
            $label = $saved.SharesLabel
            if (-not $label) { if ($saved.SharesUrl) { $label = 'Partages WinQEMU' } else { $label = '{LABEL}' } }
            $client = 'HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters'
            if (-not (Test-Path $client)) { exit 20 }
            $restart = (Get-ItemProperty $client).FileSizeLimitInBytes -ne 4294967295
            if ($restart) { Set-ItemProperty $client 'FileSizeLimitInBytes' 4294967295 -Type DWord }
            Set-Service WebClient -StartupType Automatic
            if ($restart) { Restart-Service WebClient -Force } else { Start-Service WebClient }
            New-Item 'HKLM:\SOFTWARE\WinQEMU' -Force | Out-Null
            Set-ItemProperty 'HKLM:\SOFTWARE\WinQEMU' 'SharesUrl' $url
            Set-ItemProperty 'HKLM:\SOFTWARE\WinQEMU' 'SharesLabel' $label
            try {
              $map = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('{MAP}'))
              $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -NonInteractive -EncodedCommand $map"
              $principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
              $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
              Register-ScheduledTask -TaskName 'WinQEMU Partages' -Action $action -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Principal $principal -Settings $settings -Force | Out-Null
              Start-ScheduledTask -TaskName 'WinQEMU Partages'
            } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 21 }
            Write-Output $url
            Write-Output $label
            """;

        /// <summary>The setup script for one run, with the address, the drive's name and the map script in place.</summary>
        public static string WindowsSetupFor(string url) =>
            WindowsSetupScript.Replace("{URL}", url, StringComparison.Ordinal)
                .Replace("{LABEL}", WindowsDriveLabel.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)
                .Replace("{MAP}", WindowsMapScript.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal);

        public static string Explain(GuestExecResult result) => result.ExitCode switch
        {
            10 => CoreText.GuestSharesNoUser,
            12 => string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesMountError, result.Error.Trim()),
            13 => CoreText.GuestSharesNoChannel,
            14 => CoreText.GuestSharesRelayFailed,
            15 => CoreText.GuestSharesNoRelayTool,
            20 => CoreText.GuestSharesNoWebClient,
            21 => string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesTaskFailed, result.Error.Trim()),
            _ => string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesScriptFailed, result.ExitCode, result.Error.Trim())
        };

        /// <summary>
        /// Waits for the guest agent, then mounts. Gives up on the agent after <paramref name="agentWait"/>:
        /// it may not be installed, which the report says.
        /// </summary>
        /// <param name="hasNatCard">
        /// Whether the machine has a connected NAT card: a Windows guest reaches the shares through it.
        /// Read by the caller, on the thread that may read the configuration.
        /// </param>
        /// <param name="windowsGuest">Whether the machine's profile is a Windows one, for how to install the agent.</param>
        public static async Task<GuestShareReport> MountAsync(VmSession session, TimeSpan agentWait, bool hasNatCard,
            bool windowsGuest, CancellationToken cancellationToken = default)
        {
            await using var agent = await WaitForAgentAsync(session, agentWait, cancellationToken).ConfigureAwait(false);
            if (agent is null)
            {
                // virtio-win's guest tools bring the clipboard agent but not this one, which is a separate
                // installer on the same drive.
                return new GuestShareReport(GuestShareState.NoAgent, windowsGuest
                    ? string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesNoAgentWindows, WindowsDriveLabel)
                    : string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesNoAgentLinux, GuestFolder));
            }

            var os = await agent.ExecuteAsync("guest-get-osinfo", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (os?["id"]?.GetValue<string>() == "mswindows")
                return await MountInWindowsAsync(session, agent, hasNatCard, cancellationToken).ConfigureAwait(false);
            if (os?["kernel-version"] is null)
            {
                return new GuestShareReport(GuestShareState.NotLinux, CoreText.GuestSharesUnsupportedGuest);
            }

            var result = await agent.RunAsync("/bin/sh", new[] { "-c", MountScript }, null,
                TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0
                ? new GuestShareReport(GuestShareState.Mounted, string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesMountedAt, result.Output.Trim()),
                    result.Output.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
                : new GuestShareReport(GuestShareState.Failed, string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesMountFailed, Explain(result)));
        }

        /// <summary>
        /// Windows has no 9P client, so the folders reach it over WebDAV, which Windows reads with its own
        /// client, through the machine's NAT card; nothing is installed in the guest.
        /// </summary>
        private static async Task<GuestShareReport> MountInWindowsAsync(VmSession session, GuestAgentClient agent, bool hasNatCard,
            CancellationToken cancellationToken)
        {
            if (!hasNatCard)
            {
                return new GuestShareReport(GuestShareState.Failed, CoreText.GuestSharesNeedNat);
            }

            var url = session.StartWebDavShares();
            // Encoded rather than on standard input: "-Command -" runs input line by line, which splits
            // multi-line blocks such as try/catch.
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(WindowsSetupFor(url)));
            var arguments = new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded };

            // The agent answers early in Windows' boot, while starting a program still takes longer than the
            // agent's own wait: "The guest agent did not answer guest-exec in time", seen 25 s after the
            // agent first answered. A timed-out connection is out of step for good, so each try gets a
            // fresh one.
            GuestExecResult? result = null;
            var connection = agent;
            for (var attempt = 1; result is null; attempt++)
            {
                try
                {
                    result = await connection.RunAsync("powershell.exe", arguments, null,
                        TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
                }
                catch (GuestAgentException) when (attempt < 5 && session.IsRunning)
                {
                    if (connection != agent)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                    connection = await WaitForAgentAsync(session, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false)
                        ?? throw new GuestAgentException("The guest agent stopped answering while Windows was starting.");
                }
            }
            if (connection != agent)
                await connection.DisposeAsync().ConfigureAwait(false);

            if (result.ExitCode != 0)
                return new GuestShareReport(GuestShareState.Failed, string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesOpenFailed, Explain(result)));

            // The drive's name the guest kept, on the line after the address.
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var label = lines.Length > 1 ? lines[^1] : WindowsDriveLabel;
            return new GuestShareReport(GuestShareState.Mounted, string.Format(CultureInfo.CurrentCulture, CoreText.GuestSharesDriveMapped, label));
        }

        /// <summary>
        /// A synchronised agent connection, or null when none answered in time. Each attempt uses a fresh
        /// connection: a failed synchronisation leaves the old one mid-read.
        /// </summary>
        public static async Task<GuestAgentClient?> WaitForAgentAsync(VmSession session, TimeSpan wait, CancellationToken cancellationToken = default)
        {
            var end = DateTime.UtcNow + wait;
            while (session.IsRunning)
            {
                GuestAgentClient? agent = null;
                try
                {
                    agent = await session.ConnectGuestAgentAsync(cancellationToken).ConfigureAwait(false);
                    if (await agent.SynchronizeAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
                        return agent;
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
                {
                    // QEMU going away or not listening yet: try again until the deadline.
                }

                if (agent is not null)
                    await agent.DisposeAsync().ConfigureAwait(false);
                if (DateTime.UtcNow > end)
                    return null;
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
    }
}
