using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Sharing
{
    /// <summary>A file or folder of the guest, as its file browser lists it.</summary>
    public sealed record GuestFileEntry(string Name, bool IsDirectory, long Size);

    /// <summary>
    /// Copies files and folders of the guest to this PC: the other way of <see cref="GuestFileDrop"/>, on the same
    /// virtio-serial port, with a sender the guest agent starts (Perl on Linux, C# compiled by Windows' .NET
    /// Framework on Windows). The guest is browsed through the agent (<see cref="ListAsync"/>).
    /// </summary>
    /// <remarks>
    /// The sender walks what was chosen, says the total ('T'), then sends the drop's records: 'D' a folder, 'F' a
    /// file with its length and date, then its data in chunks, 'E' the end, or 'X' and a reason. It reads as root
    /// or SYSTEM, so a file the guest's user cannot read is copied too. A name Windows refuses (':' '?' …) gets
    /// '_' instead. Cancelled or failed, what was written here is removed; the sender, writing to a port no host
    /// reads, is stopped by the next copy either way (they share the receiver's process file).
    /// </remarks>
    public static class GuestFilePull
    {
        // ---- Browsing the guest ----

        /// <summary>Where browsing starts: the guest user's desktop, else their home, else the root.</summary>
        public static async Task<string> HomeAsync(GuestAgentClient agent, bool windowsGuest, CancellationToken cancellationToken = default)
        {
            var result = windowsGuest
                ? await RunPowerShellAsync(agent, """
                    $p = (Get-CimInstance Win32_UserProfile | Where-Object { $_.Loaded -and -not $_.Special } | Select-Object -First 1).LocalPath
                    if (-not $p) { 'C:\' } elseif (Test-Path (Join-Path $p 'Desktop')) { Join-Path $p 'Desktop' } else { $p }
                    """, cancellationToken).ConfigureAwait(false)
                : await agent.RunAsync("/bin/sh", new[] { "-c", LinuxHomeScript }, null, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
            var home = result.Output.Trim();
            return home.Length > 0 ? home : windowsGuest ? @"C:\" : "/";
        }

        private const string LinuxHomeScript = """
            h=$(awk -F: '$3 >= 1000 && $3 < 60000 && $7 !~ /(nologin|false)$/ { print $6; exit }' /etc/passwd)
            [ -n "$h" ] || { echo /; exit 0; }
            d="$h/Desktop"
            if [ -f "$h/.config/user-dirs.dirs" ]; then
              x=$(sed -n 's/^XDG_DESKTOP_DIR="\(.*\)"/\1/p' "$h/.config/user-dirs.dirs" | sed "s|\$HOME|$h|")
              [ -n "$x" ] && d="$x"
            fi
            if [ -d "$d" ]; then echo "$d"; else echo "$h"; fi
            """;

        /// <summary>A folder's entries, folders first, by name. Throws GuestAgentException when it cannot be read.</summary>
        public static async Task<IReadOnlyList<GuestFileEntry>> ListAsync(GuestAgentClient agent, bool windowsGuest, string path,
            CancellationToken cancellationToken = default)
        {
            var result = windowsGuest
                ? await RunPowerShellAsync(agent, $$"""
                    $ErrorActionPreference = 'Stop'
                    Get-ChildItem -LiteralPath '{{GuestFileDrop.Quote(path)}}' -Force | ForEach-Object {
                      (@('f', 'd')[[int]$_.PSIsContainer]) + "`t" + $(if ($_.PSIsContainer) { 0 } else { $_.Length }) + "`t" + $_.Name }
                    """, cancellationToken).ConfigureAwait(false)
                : await agent.RunAsync("/bin/sh",
                    new[] { "-c", "cd -- \"$1\" && find . -mindepth 1 -maxdepth 1 -printf '%y\\t%s\\t%f\\n'", "sh", path },
                    null, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new GuestAgentException(result.Error.Trim().Length > 0 ? result.Error.Trim() : $"{path} ne peut pas être lu.");

            var entries = new List<GuestFileEntry>();
            foreach (var line in result.Output.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t', 3);
                if (parts.Length != 3 || parts[2].Length == 0)
                    continue;
                // Linux: d folder, f file, l link (followed when copied only if it leads to a file).
                entries.Add(new GuestFileEntry(parts[2], parts[0] == "d", long.TryParse(parts[1], out var size) ? size : 0));
            }
            return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string Combine(string folder, string name, bool windowsGuest) =>
            windowsGuest
                ? folder.TrimEnd('\\') + "\\" + name
                : folder == "/" ? "/" + name : folder.TrimEnd('/') + "/" + name;

        /// <summary>The folder above, or the same one at the root.</summary>
        public static string Parent(string path, bool windowsGuest)
        {
            var separator = windowsGuest ? '\\' : '/';
            var trimmed = path.TrimEnd(separator);
            var at = trimmed.LastIndexOf(separator);
            if (at < 0)
                return path;
            if (windowsGuest)
                return at <= 2 ? trimmed[..(at + 1)] : trimmed[..at]; // "C:\x" -> "C:\"
            return at == 0 ? "/" : trimmed[..at];
        }

        private static Task<GuestExecResult> RunPowerShellAsync(GuestAgentClient agent, string script, CancellationToken cancellationToken)
        {
            // UTF-8 out, or accented names come back in the console's code page.
            var full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script;
            return agent.RunAsync("powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(full)) },
                null, TimeSpan.FromSeconds(60), cancellationToken);
        }

        // ---- The senders ----

        /// <summary>Run as root by the agent: the port, then the paths to send.</summary>
        public const string LinuxSender = """
            use strict; use warnings;
            $0 = 'winqemu-pull';
            my ($port, @paths) = @ARGV;
            my $run = '/run/winqemu'; mkdir $run, 0700;
            if (open my $f, '<', "$run/drop.pid") {
              my $old = <$f> // ''; close $f; chomp $old;
              if ($old =~ /^\d+$/ && $old != $$ && open my $c, '<', "/proc/$old/cmdline") {
                local $/; my $cmd = <$c> // ''; close $c;
                if ($cmd =~ /^winqemu-(drop|pull)/) { kill 'KILL', $old; select undef, undef, undef, 0.3 }
              }
            }
            if (open my $f, '>', "$run/drop.pid") { print $f $$; close $f }
            open(my $p, '+<:raw', $port) or do { print STDERR "$port: $!\n"; exit 13 };
            sub put { my $b = shift; my $o = 0; while ($o < length $b) { my $n = syswrite($p, $b, length($b) - $o, $o); exit 1 unless defined $n; $o += $n } }
            sub fail { my $m = shift; $m =~ s/[\r\n]+/ /g; put('X' . pack('n', length $m) . $m); exit 1 }
            my @entries;
            sub walk {
              my ($abs, $rel) = @_;
              if (-d $abs && !-l $abs) {
                push @entries, ['D', $rel, $abs, 0, 0];
                opendir(my $d, $abs) or fail("$abs : $!");
                my @names = sort grep { $_ ne '.' && $_ ne '..' } readdir $d;
                closedir $d;
                walk("$abs/$_", "$rel/$_") for @names;
              } elsif (-f $abs) {
                my @st = stat $abs;
                push @entries, ['F', $rel, $abs, $st[7], $st[9]];
              }
            }
            for my $path (@paths) {
              $path =~ s{/+$}{} unless $path eq '/';
              my ($name) = $path =~ m{([^/]+)$};
              fail("$path : chemin invalide") unless defined $name;
              fail("$path : introuvable") unless -e $path;
              walk($path, $name);
            }
            my $total = 0; $total += $_->[3] for @entries;
            put('T' . pack('q>', $total));
            for my $e (@entries) {
              my ($t, $rel, $abs, $size, $mtime) = @$e;
              if ($t eq 'D') { put('D' . pack('n', length $rel) . $rel); next }
              open(my $in, '<:raw', $abs) or fail("$rel : $!");
              put('F' . pack('n', length $rel) . $rel . pack('q>q>', $size, $mtime));
              my $left = $size;
              while ($left > 0) {
                my $n = sysread($in, my $buf, $left > 1048576 ? 1048576 : $left);
                fail("$rel : $!") unless defined $n;
                fail("$rel a changé pendant la copie") if $n == 0;
                put(pack('N', $n) . $buf);
                $left -= $n;
              }
              close $in;
            }
            put('E');
            """;

        /// <summary>The Windows sender, compiled once per version as the drop's receiver is (C# 5).</summary>
        public const string WindowsSenderSource = """
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Text;
            using Microsoft.Win32.SafeHandles;

            public static class WinQemuPull
            {
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
                [DllImport("kernel32.dll", SetLastError = true)]
                static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

                static SafeFileHandle port;

                static void Put(byte[] bytes)
                {
                    var done = 0;
                    while (done < bytes.Length)
                    {
                        var part = done == 0 ? bytes : Slice(bytes, done, bytes.Length - done);
                        int written;
                        if (!WriteFile(port, part, part.Length, out written, IntPtr.Zero))
                            throw new IOException("port " + Marshal.GetLastWin32Error());
                        done += written;
                    }
                }

                static byte[] Slice(byte[] b, int at, int count) { var s = new byte[count]; Buffer.BlockCopy(b, at, s, 0, count); return s; }

                static byte[] Number(long value, int size)
                {
                    var b = new byte[size];
                    for (var i = size - 1; i >= 0; i--) { b[i] = (byte)(value & 0xFF); value >>= 8; }
                    return b;
                }

                static byte[] Join(params byte[][] parts)
                {
                    var stream = new MemoryStream();
                    foreach (var p in parts) stream.Write(p, 0, p.Length);
                    return stream.ToArray();
                }

                class Entry { public bool Folder; public string Rel; public string Path; public long Size; public long Time; }

                static void Walk(string path, string rel, List<Entry> entries)
                {
                    if (Directory.Exists(path))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && rel.Contains("/")) return;
                        entries.Add(new Entry { Folder = true, Rel = rel, Path = path });
                        var names = new List<string>(Directory.GetFileSystemEntries(path));
                        names.Sort(StringComparer.OrdinalIgnoreCase);
                        foreach (var child in names) Walk(child, rel + "/" + System.IO.Path.GetFileName(child), entries);
                    }
                    else if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        entries.Add(new Entry { Rel = rel, Path = path, Size = info.Length,
                            Time = (long)(info.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds });
                    }
                }

                static void Fail(string message)
                {
                    var bytes = Encoding.UTF8.GetBytes(message.Replace("\r", " ").Replace("\n", " "));
                    Put(Join(new[] { (byte)'X' }, Number(bytes.Length, 2), bytes));
                }

                public static int Run(string portPath, string paths)
                {
                    port = CreateFile(portPath, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (port.IsInvalid)
                    {
                        Console.Error.WriteLine(portPath + ": " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                        return 13;
                    }
                    try
                    {
                        var entries = new List<Entry>();
                        foreach (var raw in paths.Split('\n'))
                        {
                            var path = raw.Trim().TrimEnd('\\');
                            if (path.Length == 0) continue;
                            if (path.EndsWith(":")) path += "\\";
                            if (!Directory.Exists(path) && !File.Exists(path)) { Fail(path + " : introuvable"); return 1; }
                            var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                            if (name.Length == 0) { Fail(path + " : chemin invalide"); return 1; }
                            Walk(path, name, entries);
                        }
                        long total = 0;
                        foreach (var e in entries) total += e.Size;
                        Put(Join(new[] { (byte)'T' }, Number(total, 8)));
                        var buffer = new byte[1 << 20];
                        foreach (var e in entries)
                        {
                            var rel = Encoding.UTF8.GetBytes(e.Rel);
                            if (e.Folder) { Put(Join(new[] { (byte)'D' }, Number(rel.Length, 2), rel)); continue; }
                            using (var input = new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                Put(Join(new[] { (byte)'F' }, Number(rel.Length, 2), rel, Number(e.Size, 8), Number(e.Time, 8)));
                                var left = e.Size;
                                while (left > 0)
                                {
                                    var n = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                                    if (n == 0) { Fail(e.Rel + " a changé pendant la copie"); return 1; }
                                    Put(Join(Number(n, 4), Slice(buffer, 0, n)));
                                    left -= n;
                                }
                            }
                        }
                        Put(new[] { (byte)'E' });
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        try { Fail(ex.Message); } catch { }
                        return 1;
                    }
                }
            }
            """;

        /// <summary>
        /// Run as SYSTEM by the agent, as the drop's receiver script: compiles the sender if this version is not
        /// there, stops a copy left over, and sends <paramref name="paths"/>.
        /// </summary>
        public static string WindowsSenderScript(string portPath, IEnumerable<string> paths, string? workFolder = null) => """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $work = '{WORK}'
            if (-not $work) { $work = "$env:ProgramData\WinQEMU" }
            New-Item $work -ItemType Directory -Force | Out-Null
            $pidFile = "$work\drop.pid"
            # Read with care: a guest switched off as the file was written left it full of zero bytes, and
            # every later drop stopped on it ("Impossible de convertir la valeur ... en type System.Int32").
            $old = "$(Get-Content $pidFile -Raw -ErrorAction SilentlyContinue)".Trim() -split ' '
            if ($old.Count -eq 2 -and $old[0] -match '^\d+$' -and $old[1] -match '^\d+$') {
              $process = Get-Process -Id ([int]$old[0]) -ErrorAction SilentlyContinue
              if ($process -and $process.Id -ne $PID -and $process.StartTime.Ticks -eq [long]$old[1]) { Stop-Process -Id $process.Id -Force; Start-Sleep -Milliseconds 300 }
            }
            Set-Content $pidFile "$PID $((Get-Process -Id $PID).StartTime.Ticks)"
            $dll = "$work\WinQemuPull-{HASH}.dll"
            if (-not (Test-Path $dll)) {
              $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd().Trim()))
              Add-Type -TypeDefinition $source -OutputAssembly $dll -OutputType Library
            }
            Add-Type -Path $dll
            $paths = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{PATHS}'))
            exit ([WinQemuPull]::Run('{PORT}', $paths))
            """
            .Replace("{WORK}", GuestFileDrop.Quote(workFolder ?? string.Empty), StringComparison.Ordinal)
            .Replace("{HASH}", SenderHash, StringComparison.Ordinal)
            .Replace("{PATHS}", Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", paths))), StringComparison.Ordinal)
            .Replace("{PORT}", GuestFileDrop.Quote(portPath), StringComparison.Ordinal);

        /// <summary>Standard input of <see cref="WindowsSenderScript"/>: the sender's source.</summary>
        public static readonly string WindowsSenderInput = Convert.ToBase64String(Encoding.UTF8.GetBytes(WindowsSenderSource));

        private static readonly string SenderHash =
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsSenderSource)))[..12].ToLowerInvariant();

        // ---- Receiving here ----

        /// <summary>
        /// Reads a sender's records into <paramref name="destination"/>. <paramref name="senderExited"/> is asked
        /// when the port stays quiet, so that a sender that died is reported instead of waited for.
        /// </summary>
        public static async Task<DropResult> ReceiveAsync(Stream port, string destination, IProgress<DropProgress>? progress,
            Func<CancellationToken, Task<string?>> senderExited, CancellationToken cancellationToken)
        {
            var top = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            var created = new List<string>();
            string? partial = null;
            long total = 0, received = 0;
            var buffer = new byte[1 << 20];

            async Task ReadAsync(byte[] into, int count)
            {
                var done = 0;
                while (done < count)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    quiet.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        var n = await port.ReadAsync(into.AsMemory(done, count - done), quiet.Token).ConfigureAwait(false);
                        if (n == 0)
                            throw new GuestFileDropException("L'invité a cessé d'envoyer avant la fin.");
                        done += n;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (await senderExited(cancellationToken).ConfigureAwait(false) is { } died)
                            throw new GuestFileDropException($"La copie s'est interrompue dans l'invité : {died}");
                    }
                }
            }
            async Task<byte[]> TakeAsync(int count)
            {
                var bytes = new byte[count];
                await ReadAsync(bytes, count).ConfigureAwait(false);
                return bytes;
            }
            async Task<string> TextAsync() =>
                Encoding.UTF8.GetString(await TakeAsync(BinaryPrimitives.ReadUInt16BigEndian(await TakeAsync(2).ConfigureAwait(false))).ConfigureAwait(false));

            string Place(string relative)
            {
                var parts = relative.Split('/');
                if (parts.Any(p => p.Length == 0 || p == "." || p == ".."))
                    throw new GuestFileDropException($"Nom invalide envoyé par l'invité : {relative}");
                for (var i = 0; i < parts.Length; i++)
                    parts[i] = WindowsName(parts[i]);
                if (!top.TryGetValue(parts[0], out var first))
                {
                    first = parts[0];
                    for (var n = 2; File.Exists(Path.Combine(destination, first)) || Directory.Exists(Path.Combine(destination, first)); n++)
                        first = GuestFileDrop.Numbered(parts[0], n);
                    top[parts[0]] = first;
                    names.Add(first);
                    created.Add(Path.Combine(destination, first));
                }
                parts[0] = first;
                return Path.Combine(destination, Path.Combine(parts));
            }

            try
            {
                while (true)
                {
                    var type = (char)(await TakeAsync(1).ConfigureAwait(false))[0];
                    switch (type)
                    {
                        case 'T':
                            total = BinaryPrimitives.ReadInt64BigEndian(await TakeAsync(8).ConfigureAwait(false));
                            progress?.Report(new DropProgress(0, total));
                            continue;
                        case 'X':
                            throw new GuestFileDropException($"La copie a échoué dans l'invité : {await TextAsync().ConfigureAwait(false)}.");
                        case 'E':
                            return new DropResult(destination, names);
                        case 'D':
                            Directory.CreateDirectory(Place(await TextAsync().ConfigureAwait(false)));
                            continue;
                        case 'F':
                            break;
                        default:
                            throw new GuestFileDropException("Réponse inattendue de l'invité.");
                    }

                    var path = Place(await TextAsync().ConfigureAwait(false));
                    var header = await TakeAsync(16).ConfigureAwait(false);
                    var size = BinaryPrimitives.ReadInt64BigEndian(header);
                    var time = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8))).UtcDateTime;
                    partial = path;
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                    {
                        while (size > 0)
                        {
                            var length = BinaryPrimitives.ReadUInt32BigEndian(await TakeAsync(4).ConfigureAwait(false));
                            if (length == 0 || length > size || length > buffer.Length)
                                throw new GuestFileDropException("Morceau invalide envoyé par l'invité.");
                            await ReadAsync(buffer, (int)length).ConfigureAwait(false);
                            await output.WriteAsync(buffer.AsMemory(0, (int)length), cancellationToken).ConfigureAwait(false);
                            size -= length;
                            received += length;
                            progress?.Report(new DropProgress(received, total));
                        }
                    }
                    partial = null;
                    File.SetLastWriteTimeUtc(path, time);
                }
            }
            catch
            {
                try
                {
                    if (partial is not null)
                        File.Delete(partial);
                }
                catch (IOException)
                {
                }
                foreach (var item in created)
                {
                    try
                    {
                        if (Directory.Exists(item))
                            Directory.Delete(item, true);
                        else
                            File.Delete(item);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                throw;
            }
        }

        /// <summary>A guest's name made acceptable to Windows: forbidden characters as '_', no trailing dot or space.</summary>
        public static string WindowsName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
            return safe.Length == 0 ? "_" : safe;
        }

        /// <summary>
        /// Copies <paramref name="guestPaths"/> into <paramref name="destination"/> on this PC. Needs the agent;
        /// <paramref name="windowsGuest"/> picks the sender.
        /// </summary>
        public static async Task<DropResult> PullAsync(VmSession session, IReadOnlyList<string> guestPaths, string destination,
            bool windowsGuest, IProgress<DropProgress>? progress, CancellationToken cancellationToken = default)
        {
            var gate = GuestFileDrop.LockFor(session);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!session.IsRunning || session.FileDropPort is not { } dropPort)
                    throw new GuestFileDropException("La machine n'est pas démarrée.");

                // Connected first, as for a drop: in Linux, a port with no host behind it ends at once.
                using var client = new TcpClient { NoDelay = true };
                try
                {
                    await client.ConnectAsync("127.0.0.1", dropPort, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    throw new GuestFileDropException(
                        "Cette machine a été démarrée sans le canal de copie : arrêtez-la puis redémarrez-la depuis WinQEMU.");
                }

                long pid;
                await using (var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromSeconds(10), cancellationToken)
                                 .ConfigureAwait(false))
                {
                    if (agent is null)
                        throw new GuestFileDropException("L'agent invité ne répond pas : installez d'abord les outils invités.");
                    try
                    {
                        pid = windowsGuest
                            ? await agent.StartAsync("powershell.exe",
                                GuestFileDrop.PowerShellArguments(WindowsSenderScript(GuestFileDrop.WindowsPortPath, guestPaths)),
                                WindowsSenderInput, cancellationToken).ConfigureAwait(false)
                            : await agent.StartAsync("perl", new[] { "-e", LinuxSender, GuestFileDrop.LinuxPortPath }.Concat(guestPaths).ToArray(),
                                null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (GuestAgentException ex)
                    {
                        throw new GuestFileDropException($"L'envoi n'a pas pu être lancé dans l'invité : {ex.Message}");
                    }
                }

                async Task<string?> ExitedAsync(CancellationToken token)
                {
                    try
                    {
                        await using var agent = await GuestShareMounter.WaitForAgentAsync(session, TimeSpan.FromSeconds(5), token)
                            .ConfigureAwait(false);
                        if (agent is null || await agent.GetExitAsync(pid, token).ConfigureAwait(false) is not { } exit)
                            return null;
                        return exit.ExitCode == 13
                            ? "le canal de copie est absent : arrêtez la machine puis redémarrez-la depuis WinQEMU"
                            : $"l'envoi s'est arrêté (code {exit.ExitCode}) {exit.Error.Trim()}".Trim();
                    }
                    catch (Exception ex) when (ex is GuestAgentException or IOException or SocketException or InvalidOperationException)
                    {
                        return null;
                    }
                }

                Directory.CreateDirectory(destination);
                return await ReceiveAsync(client.GetStream(), destination, progress, ExitedAsync, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
