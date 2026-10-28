using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Sharing
{
    /// <summary>A file or folder to copy into the guest, by its path relative to the drop ("Photos/2024/a.jpg").</summary>
    public sealed record DropEntry(string RelativePath, string SourcePath, bool IsDirectory, long Length, DateTime LastWriteUtc);

    public sealed record DropProgress(long BytesSent, long TotalBytes);

    /// <summary>Where the items landed in the guest, under the names they got there ("rapport (2).pdf").</summary>
    public sealed record DropResult(string GuestFolder, IReadOnlyList<string> Names);

    /// <summary>A copy that could not be made, with a message for the user.</summary>
    public sealed class GuestFileDropException : Exception
    {
        public GuestFileDropException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Copies files dropped on the console onto the guest user's desktop, as VMware's drag and drop does.
    /// </summary>
    /// <remarks>
    /// The bytes travel on a virtio-serial port of their own (<see cref="QemuArgumentBuilder.FileDropPortName"/>),
    /// read in the guest by a small receiver the guest agent starts for each drop: Perl on Linux (perl-base is
    /// essential on Debian and Ubuntu), C# compiled once by Windows' own .NET Framework on Windows. Measured on
    /// Ubuntu: 33 MB/s through the port, 11 MB/s through the agent's guest-file-write, whose JSON and base64
    /// the agent parses a character at a time.
    ///
    /// The shell connects to the port before the receiver starts: Linux answers a read on a port with no host
    /// on it with end of file at once. The receiver says where it writes ("READY"), then reads records:
    /// 'D' a folder, 'F' a file in chunks, 'X' abort, 'E' end. A file's data is chunked so that a cancel can
    /// be said mid-file (length 0xFFFFFFFF), without relying on the guest noticing a closed connection.
    /// It answers "DONE" with the names given, or "FAIL" with a reason, or "ABORTED"; failed or aborted, it
    /// removes what this drop created. A name already taken on the desktop gets " (2)", as in Explorer.
    /// </remarks>
    public static class GuestFileDrop
    {
        public const string Magic = "WQDROP1";

        /// <summary>Chunk size: large enough for the port's speed, small enough for a prompt cancel.</summary>
        public const int ChunkSize = 1 << 20;

        private const uint AbortChunk = 0xFFFFFFFF;

        public const string LinuxPortPath = "/dev/virtio-ports/" + QemuArgumentBuilder.FileDropPortName;
        public const string WindowsPortPath = @"\\.\Global\" + QemuArgumentBuilder.FileDropPortName;

        /// <summary>One copy at a time per machine: the guest's port takes a single opener.</summary>
        private static readonly ConditionalWeakTable<VmSession, SemaphoreSlim> Locks = new();

        /// <summary>
        /// The entries for dropped files and folders, folders before their contents. Two dropped items with
        /// the same name (from different folders) are told apart with " (2)", or the guest would merge them.
        /// Folder links (junctions, symbolic links) are not followed: one pointing to its own parent would
        /// never end.
        /// </summary>
        public static IReadOnlyList<DropEntry> Collect(IEnumerable<string> paths)
        {
            var entries = new List<DropEntry>();
            var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths)
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                var name = Path.GetFileName(path);
                if (name.Length == 0)
                    throw new GuestFileDropException($"« {raw} » est un lecteur entier : déposez plutôt les dossiers qu'il contient.");

                var top = name;
                for (var n = 2; !topNames.Add(top); n++)
                    top = Numbered(name, n);

                if (Directory.Exists(path))
                {
                    var info = new DirectoryInfo(path);
                    entries.Add(new DropEntry(top, path, true, 0, info.LastWriteTimeUtc));
                    AddFolder(entries, info, top);
                }
                else if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    entries.Add(new DropEntry(top, path, false, info.Length, info.LastWriteTimeUtc));
                }
                else
                {
                    throw new GuestFileDropException($"« {raw} » est introuvable.");
                }
            }
            return entries;
        }

        private static void AddFolder(List<DropEntry> entries, DirectoryInfo folder, string relative)
        {
            foreach (var item in folder.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var path = relative + "/" + item.Name;
                if (item is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    entries.Add(new DropEntry(path, sub.FullName, true, 0, sub.LastWriteTimeUtc));
                    AddFolder(entries, sub, path);
                }
                else if (item is FileInfo file)
                {
                    entries.Add(new DropEntry(path, file.FullName, false, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        /// <summary>"rapport.pdf", 2 → "rapport (2).pdf"; "Photos", 2 → "Photos (2)". The receivers name the same way.</summary>
        public static string Numbered(string name, int number)
        {
            var dot = name.LastIndexOf('.');
            return dot > 0
                ? $"{name[..dot]} ({number}){name[dot..]}"
                : $"{name} ({number})";
        }

        /// <summary>
        /// Writes the records for <paramref name="entries"/>, then 'E'. Cancelled, it tells the guest to abort
        /// (between records or mid-file) before throwing. A write itself is never cancelled half-way, which
        /// would leave the guest reading the rest of a chunk that never comes; one blocked on a guest that
        /// stopped reading ends when the caller closes the stream.
        /// </summary>
        /// <param name="sent">Called with the number of file bytes sent so far.</param>
        public static async Task WriteAsync(Stream port, IReadOnlyList<DropEntry> entries, Action<long>? sent,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[4 + ChunkSize];
            long total = 0;
            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await SayAbortAsync(port, new[] { (byte)'X' }).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var name = Encoding.UTF8.GetBytes(entry.RelativePath);
                var header = new byte[1 + 2 + name.Length + (entry.IsDirectory ? 0 : 16)];
                header[0] = (byte)(entry.IsDirectory ? 'D' : 'F');
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), checked((ushort)name.Length));
                name.CopyTo(header, 3);
                if (!entry.IsDirectory)
                {
                    BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(3 + name.Length), entry.Length);
                    BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(11 + name.Length),
                        new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
                }
                await port.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
                if (entry.IsDirectory)
                    continue;

                // The length was announced when the drop was collected: a file that grew since is sent as it
                // was, one that shrank cannot be, and the guest is told to give up.
                await using var file = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                var left = entry.Length;
                while (left > 0)
                {
                    var wanted = (int)Math.Min(left, ChunkSize);
                    var read = 0;
                    while (read < wanted)
                    {
                        var n = await file.ReadAsync(buffer.AsMemory(4 + read, wanted - read), CancellationToken.None).ConfigureAwait(false);
                        if (n == 0)
                            break;
                        read += n;
                    }
                    if (read < wanted)
                    {
                        await SayAbortAsync(port, AbortChunkBytes()).ConfigureAwait(false);
                        throw new GuestFileDropException($"« {entry.SourcePath} » a changé pendant la copie.");
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await SayAbortAsync(port, AbortChunkBytes()).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)read);
                    await port.WriteAsync(buffer.AsMemory(0, 4 + read), CancellationToken.None).ConfigureAwait(false);
                    left -= read;
                    total += read;
                    sent?.Invoke(total);
                }
            }
            await port.WriteAsync(new[] { (byte)'E' }, CancellationToken.None).ConfigureAwait(false);
            await port.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private static byte[] AbortChunkBytes()
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, AbortChunk);
            return bytes;
        }

        /// <summary>Best effort: a guest that stopped reading must not hold the shell up.</summary>
        private static async Task SayAbortAsync(Stream port, byte[] record)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await port.WriteAsync(record, timeout.Token).ConfigureAwait(false);
                await port.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        /// <summary>
        /// The Linux receiver, run as root by the agent with the port and, for tests, a folder to write to.
        /// It writes as root, then gives every item to the guest's user (the first human account, as for
        /// shared folders); the desktop is the one named in the user's user-dirs.dirs (~/Bureau in French).
        /// A receiver left over from an interrupted drop still holds the port, which takes a single opener:
        /// it is stopped first.
        /// </summary>
        public const string LinuxReceiver = """
            use strict; use warnings;
            $0 = 'winqemu-drop';
            my ($port, $dest) = @ARGV;
            my $run = '/run/winqemu'; mkdir $run, 0700;
            if (open my $f, '<', "$run/drop.pid") {
              my $old = <$f> // ''; close $f; chomp $old;
              if ($old =~ /^\d+$/ && $old != $$ && open my $c, '<', "/proc/$old/cmdline") {
                local $/; my $cmd = <$c> // ''; close $c;
                if ($cmd =~ /^winqemu-drop/) { kill 'KILL', $old; select undef, undef, undef, 0.3 }
              }
            }
            if (open my $f, '>', "$run/drop.pid") { print $f $$; close $f }
            open(my $p, '+<:raw', $port) or do { print STDERR "$port: $!\n"; exit 13 };
            sub reply { my $m = shift; $m =~ s/[\r\n]+/ /g; syswrite $p, "WQDROP1 $m\n" }
            my ($uid, $gid, $home);
            while (my @pw = getpwent) {
              if ($pw[2] >= 1000 && $pw[2] < 60000 && $pw[8] !~ /(nologin|false)$/) { ($uid, $gid, $home) = @pw[2, 3, 7]; last }
            }
            endpwent;
            unless (defined $dest && length $dest) {
              unless (defined $uid) { reply("FAIL l'invité n'a aucun compte utilisateur"); exit 10 }
              $dest = "$home/Desktop";
              if (open my $d, '<', "$home/.config/user-dirs.dirs") {
                while (<$d>) { if (/^XDG_DESKTOP_DIR="(.*)"/) { ($dest = $1) =~ s/\$HOME/$home/ } }
                close $d;
              }
              unless (-d $dest) { mkdir $dest, 0755; chown $uid, $gid, $dest }
            }
            unless (-d $dest) { reply("FAIL le dossier $dest est introuvable"); exit 11 }
            reply("READY $dest");
            sub take {
              my $n = shift; my $b = '';
              while (length($b) < $n) { my $r = sysread($p, $b, $n - length($b), length($b)); die "EOF\n" unless $r }
              return $b;
            }
            sub numbered { my ($n, $i) = @_; return $n =~ /^(.+)(\.[^.]+)$/ ? "$1 ($i)$2" : "$n ($i)" }
            my (%top, @names, @created, $partial);
            sub place {
              my @parts = split m{/}, shift, -1;
              for (@parts) { die "nom invalide\n" if $_ eq '' || $_ eq '.' || $_ eq '..' || /\0/ }
              my $first = shift @parts;
              unless (exists $top{$first}) {
                my $name = $first;
                for (my $i = 2; -e "$dest/$name" || -l "$dest/$name"; $i++) { $name = numbered($first, $i) }
                $top{$first} = $name; push @names, $name; push @created, "$dest/$name";
              }
              return join '/', $dest, $top{$first}, @parts;
            }
            my $ok = eval {
              while (1) {
                my $t = take(1);
                last if $t eq 'E';
                die "ABORT\n" if $t eq 'X';
                my $rel = take(unpack 'n', take(2));
                my $path = place($rel);
                if ($t eq 'D') {
                  mkdir $path, 0755 or -d $path or die "$rel : $!\n";
                  chown $uid, $gid, $path if defined $uid;
                  next;
                }
                die "enregistrement inconnu\n" unless $t eq 'F';
                my ($size, $mtime) = unpack 'q>q>', take(16);
                $partial = $path;
                open my $out, '>:raw', $path or die "$rel : $!\n";
                while ($size > 0) {
                  my $n = unpack 'N', take(4);
                  die "ABORT\n" if $n == 0xFFFFFFFF;
                  die "morceau invalide\n" if $n == 0 || $n > $size;
                  print $out take($n) or die "$rel : $!\n";
                  $size -= $n;
                }
                close $out or die "$rel : $!\n";
                undef $partial;
                chown $uid, $gid, $path if defined $uid;
                utime $mtime, $mtime, $path;
              }
              1;
            };
            if ($ok) { reply("DONE " . join("\t", @names)); exit 0 }
            my $why = $@; chomp $why;
            unlink $partial if defined $partial;
            system('rm', '-rf', '--', @created) if @created;
            if ($why eq 'ABORT') { reply('ABORTED'); exit 0 }
            reply("FAIL $why");
            exit 1;
            """;

        /// <summary>
        /// The Windows receiver, compiled once per version by Windows' own .NET Framework (C# 5: no string
        /// interpolation, no ?.). Run as SYSTEM by the agent; it writes as the user of the console session
        /// (impersonated), so the files are theirs, on their desktop wherever it is (OneDrive included:
        /// SHGetKnownFolderPath with their token). The port is opened with CreateFile, which .NET Framework's
        /// FileStream refuses for \\.\ paths.
        /// </summary>
        public const string WindowsReceiverSource = """
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Security.Principal;
            using System.Text;
            using Microsoft.Win32.SafeHandles;

            public static class WinQemuDrop
            {
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
                [DllImport("kernel32.dll", SetLastError = true)]
                static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);
                [DllImport("kernel32.dll", SetLastError = true)]
                static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);
                [DllImport("kernel32.dll")]
                static extern uint WTSGetActiveConsoleSessionId();
                [DllImport("wtsapi32.dll", SetLastError = true)]
                static extern bool WTSQueryUserToken(uint session, out IntPtr token);
                [DllImport("shell32.dll")]
                static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

                class Abort : Exception { }

                static SafeFileHandle port;
                static byte[] chunk = new byte[1 << 20];

                static void Reply(string message)
                {
                    var bytes = Encoding.UTF8.GetBytes("WQDROP1 " + message.Replace("\r", " ").Replace("\n", " ") + "\n");
                    int written;
                    WriteFile(port, bytes, bytes.Length, out written, IntPtr.Zero);
                }

                static void Take(byte[] buffer, int count)
                {
                    var done = 0;
                    while (done < count)
                    {
                        int read;
                        if (!ReadFile(port, chunk, Math.Min(count - done, chunk.Length), out read, IntPtr.Zero) || read == 0)
                            throw new EndOfStreamException("EOF");
                        Buffer.BlockCopy(chunk, 0, buffer, done, read);
                        done += read;
                    }
                }

                static byte[] Take(int count) { var b = new byte[count]; Take(b, count); return b; }

                static long Number(byte[] b, int at, int size)
                {
                    long value = 0;
                    for (var i = 0; i < size; i++) value = (value << 8) | b[at + i];
                    return value;
                }

                static string Numbered(string name, int i)
                {
                    var dot = name.LastIndexOf('.');
                    return dot > 0 ? name.Substring(0, dot) + " (" + i + ")" + name.Substring(dot) : name + " (" + i + ")";
                }

                public static int Run(string portPath, string dest)
                {
                    port = CreateFile(portPath, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (port.IsInvalid)
                    {
                        Console.Error.WriteLine(portPath + ": " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                        return 13;
                    }

                    WindowsImpersonationContext impersonation = null;
                    if (string.IsNullOrEmpty(dest))
                    {
                        IntPtr token;
                        var session = WTSGetActiveConsoleSessionId();
                        if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out token))
                        {
                            Reply("FAIL aucune session n'est ouverte dans l'invité : ouvrez une session Windows puis recommencez");
                            return 10;
                        }
                        IntPtr folder;
                        if (SHGetKnownFolderPath(new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), 0, token, out folder) != 0)
                        {
                            Reply("FAIL le Bureau de l'utilisateur est introuvable");
                            return 11;
                        }
                        dest = Marshal.PtrToStringUni(folder);
                        Marshal.FreeCoTaskMem(folder);
                        impersonation = new WindowsIdentity(token).Impersonate();
                    }
                    if (!Directory.Exists(dest))
                    {
                        Reply("FAIL le dossier " + dest + " est introuvable");
                        return 11;
                    }
                    Reply("READY " + dest);

                    var top = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var names = new List<string>();
                    var created = new List<string>();
                    string partial = null;
                    var invalid = Path.GetInvalidFileNameChars();
                    try
                    {
                        while (true)
                        {
                            var type = (char)Take(1)[0];
                            if (type == 'E') break;
                            if (type == 'X') throw new Abort();
                            var rel = Encoding.UTF8.GetString(Take((int)Number(Take(2), 0, 2)));
                            var parts = rel.Split('/');
                            foreach (var part in parts)
                                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(invalid) >= 0)
                                    throw new IOException("nom invalide : " + rel);
                            string first;
                            if (!top.TryGetValue(parts[0], out first))
                            {
                                first = parts[0];
                                for (var i = 2; File.Exists(Path.Combine(dest, first)) || Directory.Exists(Path.Combine(dest, first)); i++)
                                    first = Numbered(parts[0], i);
                                top[parts[0]] = first;
                                names.Add(first);
                                created.Add(Path.Combine(dest, first));
                            }
                            parts[0] = first;
                            var path = Path.Combine(dest, string.Join("\\", parts));
                            if (type == 'D')
                            {
                                Directory.CreateDirectory(path);
                                continue;
                            }
                            if (type != 'F') throw new IOException("enregistrement inconnu");
                            var header = Take(16);
                            var size = Number(header, 0, 8);
                            var mtime = Number(header, 8, 8);
                            partial = path;
                            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                            {
                                var data = new byte[1 << 20];
                                while (size > 0)
                                {
                                    var n = Number(Take(4), 0, 4);
                                    if (n == 0xFFFFFFFF) throw new Abort();
                                    if (n == 0 || n > size || n > data.Length) throw new IOException("morceau invalide");
                                    Take(data, (int)n);
                                    output.Write(data, 0, (int)n);
                                    size -= n;
                                }
                            }
                            partial = null;
                            File.SetLastWriteTimeUtc(path, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtime));
                        }
                        Reply("DONE " + string.Join("\t", names.ToArray()));
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        try { if (partial != null) File.Delete(partial); } catch { }
                        foreach (var item in created)
                        {
                            try { if (Directory.Exists(item)) Directory.Delete(item, true); else File.Delete(item); } catch { }
                        }
                        if (ex is Abort) { Reply("ABORTED"); return 0; }
                        Reply("FAIL " + ex.Message);
                        return 1;
                    }
                    finally
                    {
                        if (impersonation != null) impersonation.Undo();
                    }
                }
            }
            """;

        /// <summary>
        /// Run as SYSTEM by the agent: compiles the receiver if this version is not there yet, stops one left
        /// over from an interrupted drop (it would still hold the port), and runs it. {WORK} is where the
        /// receiver and its process id are kept (%ProgramData%\WinQEMU in a guest, a temporary folder in tests).
        /// </summary>
        /// <remarks>
        /// The source comes on standard input (<see cref="WindowsReceiverInput"/>), in base64 so the console's
        /// code page leaves its accents alone. Inside the command, it would take the encoded command past
        /// Windows' limit of 32767 characters for a command line.
        /// </remarks>
        public static string WindowsReceiverScript(string portPath, string destination, string? workFolder = null) => """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $work = '{WORK}'
            if (-not $work) { $work = "$env:ProgramData\WinQEMU" }
            New-Item $work -ItemType Directory -Force | Out-Null
            $pidFile = "$work\drop.pid"
            if (Test-Path $pidFile) {
              $old = (Get-Content $pidFile -Raw).Trim() -split ' '
              $process = Get-Process -Id ([int]$old[0]) -ErrorAction SilentlyContinue
              if ($process -and $process.Id -ne $PID -and $process.StartTime.Ticks -eq [long]$old[1]) { Stop-Process -Id $process.Id -Force; Start-Sleep -Milliseconds 300 }
            }
            Set-Content $pidFile "$PID $((Get-Process -Id $PID).StartTime.Ticks)"
            $dll = "$work\WinQemuDrop-{HASH}.dll"
            if (-not (Test-Path $dll)) {
              $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd().Trim()))
              Add-Type -TypeDefinition $source -OutputAssembly $dll -OutputType Library
            }
            Add-Type -Path $dll
            exit ([WinQemuDrop]::Run('{PORT}', '{DEST}'))
            """
            .Replace("{WORK}", Quote(workFolder ?? string.Empty), StringComparison.Ordinal)
            .Replace("{HASH}", SourceHash, StringComparison.Ordinal)
            .Replace("{PORT}", Quote(portPath), StringComparison.Ordinal)
            .Replace("{DEST}", Quote(destination), StringComparison.Ordinal);

        /// <summary>Standard input of <see cref="WindowsReceiverScript"/>: the receiver's source.</summary>
        public static readonly string WindowsReceiverInput = Convert.ToBase64String(Encoding.UTF8.GetBytes(WindowsReceiverSource));

        /// <summary>The receiver's file name changes with its source, so a guest never runs an older one.</summary>
        private static readonly string SourceHash =
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsReceiverSource)))[..12].ToLowerInvariant();

        /// <summary>For a single-quoted PowerShell string.</summary>
        private static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);

        /// <summary>PowerShell's arguments for <see cref="WindowsReceiverScript"/>.</summary>
        public static string[] PowerShellArguments(string script) =>
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) };

        /// <summary>
        /// Talks to a receiver over its port: waits for READY, sends the entries, and reads the outcome.
        /// <paramref name="receiverExited"/> is asked when the receiver goes quiet, so that one that died (or
        /// never started) is reported instead of waited for: the port gives no sign of it.
        /// </summary>
        public static async Task<DropResult> TransferAsync(Stream port, IReadOnlyList<DropEntry> entries,
            Func<CancellationToken, Task<string?>> receiverExited, IProgress<DropProgress>? progress, CancellationToken cancellationToken)
        {
            var lines = new LineReader(port);
            var ready = await AwaitLineAsync(lines, receiverExited, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            var destination = Parse(ready, "READY");

            var total = entries.Where(e => !e.IsDirectory).Sum(e => e.Length);
            long sent = 0;
            progress?.Report(new DropProgress(0, total));

            // The guest answers early only to say it failed (disk full, access denied), while the shell may
            // be blocked writing to it: that answer is watched for during the whole transfer.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var answer = lines.ReadLineAsync(CancellationToken.None);
            var writing = WriteAsync(port, entries, n =>
            {
                Interlocked.Exchange(ref sent, n);
                progress?.Report(new DropProgress(n, total));
            }, stop.Token);

            string? died = null;
            var lastSent = -1L;
            var quietSince = DateTime.UtcNow;
            DateTime? cancelledAt = null;
            while (!writing.IsCompleted)
            {
                var finished = await Task.WhenAny(writing, answer, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
                if (finished == answer)
                {
                    // An early answer can only be a failure: stop writing (a blocked write ends with the stream).
                    stop.Cancel();
                    port.Dispose();
                    break;
                }

                // Cancelled while a write is blocked: the abort cannot get through, so the stream goes.
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelledAt ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - cancelledAt > TimeSpan.FromSeconds(10))
                    {
                        port.Dispose();
                        break;
                    }
                }

                var now = Interlocked.Read(ref sent);
                if (now != lastSent)
                {
                    lastSent = now;
                    quietSince = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(15))
                {
                    died = await receiverExited(cancellationToken).ConfigureAwait(false);
                    quietSince = DateTime.UtcNow;
                    if (died is not null)
                    {
                        stop.Cancel();
                        port.Dispose();
                        break;
                    }
                }
            }

            try
            {
                await writing.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (died is not null)
                    throw new GuestFileDropException($"La copie s'est interrompue dans l'invité : {died}");
                if (answer.IsCompletedSuccessfully && answer.Result is { } early)
                    throw new GuestFileDropException(Failure(early));
                if (cancellationToken.IsCancellationRequested)
                {
                    // Give the guest a moment to remove what it had written; it says ABORTED when done.
                    await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);
                }
                throw;
            }

            var outcome = await Task.WhenAny(answer, Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None)).ConfigureAwait(false) == answer
                ? await answer.ConfigureAwait(false)
                : null;
            var done = Parse(outcome, "DONE");
            return new DropResult(destination, done.Length == 0 ? Array.Empty<string>() : done.Split('\t'));
        }

        /// <summary>The next line from the receiver, asking whether it died each time it stays quiet a while.</summary>
        private static async Task<string?> AwaitLineAsync(LineReader lines, Func<CancellationToken, Task<string?>> receiverExited,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var line = lines.ReadLineAsync(cancellationToken);
            var end = DateTime.UtcNow + timeout;
            while (true)
            {
                if (await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false) == line)
                    return await line.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (await receiverExited(cancellationToken).ConfigureAwait(false) is { } died)
                    throw new GuestFileDropException($"La copie n'a pas pu commencer dans l'invité : {died}");
                if (DateTime.UtcNow > end)
                    throw new GuestFileDropException("L'invité n'a pas répondu à temps pour recevoir les fichiers.");
            }
        }

        /// <summary>The text after "WQDROP1 <paramref name="expected"/>", or the failure the line reports instead.</summary>
        private static string Parse(string? line, string expected)
        {
            var prefix = $"{Magic} {expected}";
            if (line is not null && line.StartsWith(prefix, StringComparison.Ordinal))
                return line.Length > prefix.Length ? line[(prefix.Length + 1)..] : string.Empty;
            throw new GuestFileDropException(Failure(line));
        }

        private static string Failure(string? line)
        {
            var prefix = $"{Magic} FAIL ";
            return line is null
                ? "L'invité a cessé de répondre pendant la copie."
                : line.StartsWith(prefix, StringComparison.Ordinal)
                    ? $"La copie a échoué dans l'invité : {line[prefix.Length..]}."
                    : $"Réponse inattendue de l'invité : {line}";
        }

        /// <summary>
        /// Copies <paramref name="entries"/> onto the desktop of the guest's user. Needs the guest agent;
        /// <paramref name="windowsGuest"/> (the machine's profile) only shapes the message when there is none.
        /// </summary>
        public static async Task<DropResult> SendAsync(VmSession session, IReadOnlyList<DropEntry> entries, bool windowsGuest,
            IProgress<DropProgress>? progress, CancellationToken cancellationToken = default)
        {
            var gate = Locks.GetValue(session, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!session.IsRunning || session.FileDropPort is not { } dropPort)
                    throw new GuestFileDropException("La machine n'est pas démarrée.");

                // Connected first: in Linux, a read on a port with no host behind it ends at once.
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
                    {
                        throw new GuestFileDropException(windowsGuest
                            ? "L'agent invité ne répond pas. Installez-le une fois dans Windows, depuis le lecteur virtio-win : " +
                              @"guest-agent\qemu-ga-x86_64.msi."
                            : "L'agent invité ne répond pas. Installez-le une fois dans l'invité (sudo apt install qemu-guest-agent).");
                    }

                    var os = await agent.ExecuteAsync("guest-get-osinfo", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        pid = os?["id"]?.GetValue<string>() == "mswindows"
                            ? await agent.StartAsync("powershell.exe",
                                PowerShellArguments(WindowsReceiverScript(WindowsPortPath, string.Empty)), WindowsReceiverInput,
                                cancellationToken).ConfigureAwait(false)
                            : await agent.StartAsync("perl", new[] { "-e", LinuxReceiver, LinuxPortPath }, null, cancellationToken)
                                .ConfigureAwait(false);
                    }
                    catch (GuestAgentException ex)
                    {
                        throw new GuestFileDropException($"Le récepteur n'a pas pu être lancé dans l'invité : {ex.Message}");
                    }
                }

                // The agent is let go during the copy: the display and the charts share its single channel.
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
                            : $"le récepteur s'est arrêté (code {exit.ExitCode}) {exit.Error.Trim()}".Trim();
                    }
                    catch (Exception ex) when (ex is GuestAgentException or IOException or SocketException or InvalidOperationException)
                    {
                        return null;
                    }
                }

                var stream = client.GetStream();
                return await TransferAsync(stream, entries, ExitedAsync, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Reads UTF-8 lines a byte at a time, so nothing past a line is taken from the port.</summary>
        private sealed class LineReader
        {
            private readonly Stream _stream;

            public LineReader(Stream stream) => _stream = stream;

            public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                var bytes = new List<byte>();
                var one = new byte[1];
                while (true)
                {
                    int n;
                    try
                    {
                        n = await _stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
                    {
                        return null;
                    }
                    if (n == 0)
                        return null;
                    if (one[0] == (byte)'\n')
                        return Encoding.UTF8.GetString(bytes.ToArray());
                    bytes.Add(one[0]);
                }
            }
        }
    }
}
