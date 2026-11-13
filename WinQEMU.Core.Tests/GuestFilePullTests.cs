using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using WinQEMU.Core.Sharing;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Files copied from the guest: the Windows sender compiled and run here by Windows PowerShell, a named pipe in
/// place of the guest's port, and WinQEMU's receiver writing what it gets.
/// </summary>
public sealed class GuestFilePullTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-pull-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("/home/a/Bureau", false, "/home/a")]
    [InlineData("/home", false, "/")]
    [InlineData("/", false, "/")]
    [InlineData(@"C:\Users\a\Desktop", true, @"C:\Users\a")]
    [InlineData(@"C:\Users", true, @"C:\")]
    [InlineData(@"C:\", true, @"C:\")]
    public void The_folder_above(string path, bool windows, string expected)
    {
        Assert.Equal(expected, GuestFilePull.Parent(path, windows));
    }

    [Theory]
    [InlineData("rapport?.txt", "rapport_.txt")]
    [InlineData("a:b", "a_b")]
    [InlineData("fin.", "fin")]
    [InlineData("normal été.pdf", "normal été.pdf")]
    public void A_guest_name_Windows_refuses_is_made_acceptable(string name, string expected)
    {
        Assert.Equal(expected, GuestFilePull.WindowsName(name));
    }

    [Fact]
    public void The_linux_sender_is_valid_perl()
    {
        var perl = new[] { @"C:\Program Files\Git\usr\bin\perl.exe" }.Concat(
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Select(f => Path.Combine(f.Trim('"'), "perl.exe")))
            .FirstOrDefault(File.Exists);
        if (perl is null)
            return;
        var script = Path.Combine(_folder, "sender.pl");
        File.WriteAllText(script, GuestFilePull.LinuxSender);
        using var check = Process.Start(new ProcessStartInfo(perl) { ArgumentList = { "-c", script }, RedirectStandardError = true, UseShellExecute = false })!;
        var output = check.StandardError.ReadToEnd();
        check.WaitForExit();
        Assert.True(check.ExitCode == 0, output);
    }

    [Fact]
    public async Task A_folder_and_a_file_come_over_whole_and_a_name_taken_is_numbered()
    {
        var guest = Directory.CreateDirectory(Path.Combine(_folder, "guest")).FullName;
        var photos = Directory.CreateDirectory(Path.Combine(guest, "Photos", "été")).FullName;
        var picture = new byte[(1 << 20) * 2 + 999];
        new Random(5).NextBytes(picture);
        File.WriteAllBytes(Path.Combine(photos, "b.bin"), picture);
        File.SetLastWriteTimeUtc(Path.Combine(photos, "b.bin"), new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(guest, "Photos", "vide"));
        File.WriteAllText(Path.Combine(guest, "note.txt"), "depuis l'invité");
        var here = Directory.CreateDirectory(Path.Combine(_folder, "here")).FullName;
        File.WriteAllText(Path.Combine(here, "note.txt"), "déjà là");

        var result = await PullAsync(new[] { Path.Combine(guest, "Photos"), Path.Combine(guest, "note.txt") }, here, CancellationToken.None);

        Assert.Equal(new[] { "Photos", "note (2).txt" }, result.Names);
        Assert.Equal(picture, File.ReadAllBytes(Path.Combine(here, "Photos", "été", "b.bin")));
        Assert.Equal(new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(here, "Photos", "été", "b.bin")));
        Assert.True(Directory.Exists(Path.Combine(here, "Photos", "vide")));
        Assert.Equal("depuis l'invité", File.ReadAllText(Path.Combine(here, "note (2).txt")));
        Assert.Equal("déjà là", File.ReadAllText(Path.Combine(here, "note.txt")));
    }

    [Fact]
    public async Task A_missing_path_is_said_and_nothing_is_left()
    {
        var here = Directory.CreateDirectory(Path.Combine(_folder, "here")).FullName;

        var error = await Assert.ThrowsAsync<GuestFileDropException>(() =>
            PullAsync(new[] { Path.Combine(_folder, "absent") }, here, CancellationToken.None));

        Assert.Contains("introuvable", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(here));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_nothing_here()
    {
        var guest = Directory.CreateDirectory(Path.Combine(_folder, "guest")).FullName;
        File.WriteAllBytes(Path.Combine(guest, "big.bin"), new byte[(1 << 20) * 16]);
        var here = Directory.CreateDirectory(Path.Combine(_folder, "here")).FullName;
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PullAsync(new[] { Path.Combine(guest, "big.bin") }, here, cancel.Token, p =>
            {
                if (p.BytesSent > 1 << 20)
                    cancel.Cancel();
            }));

        Assert.Empty(Directory.EnumerateFileSystemEntries(here));
    }

    /// <summary>The sender as a guest's agent would run it, on a named pipe instead of the port.</summary>
    private async Task<DropResult> PullAsync(IReadOnlyList<string> paths, string destination, CancellationToken cancellationToken,
        Action<DropProgress>? onProgress = null)
    {
        var name = "winqemu-pull-test-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var marker = GuestFilePull.NewMarker();
        var script = GuestFilePull.WindowsSenderScript($@"\\.\pipe\{name}", paths, marker, Path.Combine(_folder, "work"));
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in GuestFileDrop.PowerShellArguments(script))
            start.ArgumentList.Add(argument);
        using var sender = Process.Start(start)!;
        var errors = sender.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = sender.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await sender.StandardInput.WriteAsync(GuestFilePull.WindowsSenderInput);
        sender.StandardInput.Close();

        using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var waiting = pipe.WaitForConnectionAsync(connect.Token);
            if (await Task.WhenAny(waiting, sender.WaitForExitAsync(connect.Token)) != waiting)
                throw new Xunit.Sdk.XunitException("The sender ended before opening the pipe: " + await errors);
            await waiting;
        }

        try
        {
            return await GuestFilePull.ReceiveAsync(pipe, destination, onProgress is null ? null : new Progress(onProgress),
                async _ => sender.HasExited ? $"code {sender.ExitCode} {await errors}" : null, cancellationToken, marker);
        }
        catch when (!sender.HasExited)
        {
            // As PullAsync does: the sender, perhaps in the middle of a write, is stopped while the pipe is read.
            var stopping = Stopwatch.StartNew();
            await GuestFilePull.StopSenderWhileDrainingAsync(pipe, () =>
            {
                sender.Kill();
                return sender.WaitForExitAsync();
            });
            Assert.True(sender.HasExited);
            Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(10), $"stopping took {stopping.Elapsed}");
            throw;
        }
        finally
        {
            pipe.Dispose();
            if (!sender.WaitForExit(10000))
                sender.Kill();
        }
    }

    [Fact]
    public async Task What_comes_before_the_copys_marker_is_skipped()
    {
        // Bytes left on the way by a copy stopped before: a chunk header and data, an older marker, then this copy's.
        var marker = GuestFilePull.NewMarker();
        var content = Encoding.UTF8.GetBytes("bonjour");
        var stream = new MemoryStream();
        void Write(params byte[][] parts)
        {
            foreach (var part in parts)
                stream.Write(part);
        }
        byte[] Number(long value, int size)
        {
            var bytes = new byte[size];
            for (var i = size - 1; i >= 0; i--, value >>= 8)
                bytes[i] = (byte)value;
            return bytes;
        }
        var name = Encoding.UTF8.GetBytes("note.txt");
        Write(Number(1 << 20, 4), new byte[300], Encoding.ASCII.GetBytes(GuestFilePull.NewMarker()), "FT"u8.ToArray(),
            Encoding.ASCII.GetBytes(marker),
            "T"u8.ToArray(), Number(content.Length, 8),
            "F"u8.ToArray(), Number(name.Length, 2), name, Number(content.Length, 8), Number(0, 8),
            Number(content.Length, 4), content, "E"u8.ToArray());
        stream.Position = 0;
        var here = Directory.CreateDirectory(Path.Combine(_folder, "here")).FullName;

        var result = await GuestFilePull.ReceiveAsync(stream, here, null, _ => Task.FromResult<string?>(null), CancellationToken.None, marker);

        Assert.Equal(new[] { "note.txt" }, result.Names);
        Assert.Equal("bonjour", File.ReadAllText(Path.Combine(here, "note.txt")));
    }

    /// <summary>A sender's records, as the guest programs write them.</summary>
    private sealed class Records
    {
        private readonly MemoryStream _stream = new();

        private static byte[] Number(long value, int size)
        {
            var bytes = new byte[size];
            for (var i = size - 1; i >= 0; i--, value >>= 8)
                bytes[i] = (byte)value;
            return bytes;
        }

        private Records Text(string type, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _stream.Write(Encoding.ASCII.GetBytes(type));
            _stream.Write(Number(bytes.Length, 2));
            _stream.Write(bytes);
            return this;
        }

        public Records Folder(string path) => Text("D", path);

        /// <param name="sent">What is sent of the content before the sender goes on; all of it by default.</param>
        public Records File(string path, string content, int? sent = null)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            Text("F", path);
            _stream.Write(Number(bytes.Length, 8));
            _stream.Write(Number(0, 8));
            var count = sent ?? bytes.Length;
            if (count > 0)
            {
                _stream.Write(Number(count, 4));
                _stream.Write(bytes, 0, count);
            }
            return this;
        }

        public Records Failure(string message) => Text("X", message);

        public Stream End(bool finished = true)
        {
            if (finished)
                _stream.Write("E"u8);
            _stream.Position = 0;
            return _stream;
        }
    }

    // Under the first level, the second of the two failed on "the file exists" and the whole copy was removed; at the first
    // level the two folders were merged into one.
    [Fact]
    public async Task Two_names_of_the_guest_that_make_one_for_Windows_are_both_kept()
    {
        var here = Directory.CreateDirectory(Path.Combine(_folder, "here")).FullName;
        var records = new Records()
            .Folder("src").File("src/Makefile", "upper").File("src/makefile", "lower")
            .Folder("a:b").File("a:b/one", "1")
            .Folder("a?b").File("a?b/two", "2")
            .End();

        var result = await GuestFilePull.ReceiveAsync(records, here, null, _ => Task.FromResult<string?>(null), CancellationToken.None);

        Assert.Equal(new[] { "src", "a_b", "a_b (2)" }, result.Names);
        Assert.Equal("upper", File.ReadAllText(Path.Combine(here, "src", "Makefile")));
        Assert.Equal("lower", File.ReadAllText(Path.Combine(here, "src", "makefile (2)")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(here, "a_b", "one")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(here, "a_b (2)", "two")));
    }

    // Read as a chunk's length, the sender's "X" record gave "invalid chunk (58…)", which looked like lost bytes.
    [Fact]
    public async Task A_sender_that_gives_up_inside_a_file_is_reported_with_its_own_message()
    {
        var here = Directory.CreateDirectory(Path.Combine(_folder, "here")).FullName;
        var records = new Records().File("log.txt", "0123456789", sent: 4).Failure("#changed /var/log/log.txt").End(finished: false);

        var error = await Assert.ThrowsAsync<GuestFileDropException>(() =>
            GuestFilePull.ReceiveAsync(records, here, null, _ => Task.FromResult<string?>(null), CancellationToken.None));

        Assert.Contains("/var/log/log.txt", error.Message);
        Assert.DoesNotContain("58", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(here));
    }

    private sealed class Progress : IProgress<DropProgress>
    {
        private readonly Action<DropProgress> _report;

        public Progress(Action<DropProgress> report) => _report = report;

        public void Report(DropProgress value) => _report(value);
    }
}
