using WinQEMU.Core.Qemu;

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

    public sealed record GuestShareReport(GuestShareState State, string Text);

    /// <summary>
    /// Mounts the shared folders' private channel inside a Linux guest, through qemu-guest-agent, so they
    /// are simply there in ~/Partages, as VMware's are in /mnt/hgfs.
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
        /// <summary>Folder, under the guest user's home, that shared folders appear in.</summary>
        public const string GuestFolder = "Partages";

        /// <summary>
        /// Run as root by the agent. Prints the mount point; exit codes 10-15 are explained by <see cref="Explain"/>.
        /// </summary>
        /// <remarks>
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
        public const string MountScript = """
            set -u
            user=$(awk -F: '$3 >= 1000 && $3 < 60000 && $7 !~ /(nologin|false)$/ { print $1; exit }' /etc/passwd)
            [ -n "$user" ] || exit 10
            home=$(getent passwd "$user" | cut -d: -f6); uid=$(id -u "$user"); gid=$(id -g "$user")
            dir="$home/Partages"
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

        public static string Explain(GuestExecResult result) => result.ExitCode switch
        {
            10 => "l'invité n'a aucun compte utilisateur où les placer",
            12 => "le montage a échoué : " + result.Error.Trim(),
            13 => "la machine n'a pas le canal des dossiers partagés : arrêtez-la puis redémarrez-la depuis WinQEMU",
            14 => "le relais du canal n'a pas pu démarrer dans /run/winqemu",
            15 => "l'invité n'a ni perl ni python3 pour relayer le canal",
            _ => $"le script s'est arrêté avec le code {result.ExitCode} : {result.Error.Trim()}"
        };

        /// <summary>
        /// Waits for the guest agent, then mounts. Gives up on the agent after <paramref name="agentWait"/>:
        /// it may not be installed, which the report says.
        /// </summary>
        public static async Task<GuestShareReport> MountAsync(VmSession session, TimeSpan agentWait, CancellationToken cancellationToken = default)
        {
            await using var agent = await WaitForAgentAsync(session, agentWait, cancellationToken).ConfigureAwait(false);
            if (agent is null)
            {
                return new GuestShareReport(GuestShareState.NoAgent,
                    "L'agent invité ne répond pas. Installez-le une fois dans l'invité (sudo apt install " +
                    "qemu-guest-agent) et redémarrez-le : les dossiers apparaîtront dans ~/Partages.");
            }

            var os = await agent.ExecuteAsync("guest-get-osinfo", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (os?["kernel-version"] is null || os["id"]?.GetValue<string>() == "mswindows")
            {
                return new GuestShareReport(GuestShareState.NotLinux,
                    "Les dossiers partagés ne s'ouvrent que dans les invités Linux pour l'instant : Windows n'a pas " +
                    "de client 9P, il lui faudra un outil WinQEMU à installer, comme les VMware Tools.");
            }

            var result = await agent.RunAsync("/bin/sh", new[] { "-c", MountScript }, null,
                TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0
                ? new GuestShareReport(GuestShareState.Mounted, $"Dans l'invité : {result.Output.Trim()}")
                : new GuestShareReport(GuestShareState.Failed, $"Les dossiers n'ont pas pu être montés dans l'invité : {Explain(result)}.");
        }

        /// <summary>
        /// A synchronised agent connection, or null when none answered in time. Each attempt uses a fresh
        /// connection: a failed synchronisation leaves the old one mid-read.
        /// </summary>
        private static async Task<GuestAgentClient?> WaitForAgentAsync(VmSession session, TimeSpan wait, CancellationToken cancellationToken)
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
