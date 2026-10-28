using System.Diagnostics;
using System.IO.Pipes;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Sharing;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Files dropped on the console: the channel, what is sent, and the Windows receiver itself, compiled and run
/// here by Windows PowerShell with a named pipe in place of the guest's port.
/// </summary>
public sealed class GuestFileDropTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-drop-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void The_drop_port_joins_the_guest_channels_on_their_controller()
    {
        var args = QemuArgumentBuilder.Build(Fixtures.Vm(), Fixtures.Installation(), 4444, 50000,
            guestAgentPort: 4555, sharedFoldersPort: 4556, fileDropPort: 4557);
        var devices = Fixtures.Values(args, "-device");

        Assert.Equal("virtio-serial-pci,id=wq-qga-bus", devices[^4]);
        Assert.Equal("virtserialport,bus=wq-qga-bus.0,chardev=wq-drop,name=org.winqemu.drop.0", devices[^1]);
        Assert.Contains("socket,id=wq-drop,host=127.0.0.1,port=4557,server=on,wait=off", Fixtures.Values(args, "-chardev"));
        Assert.Equal(1, devices.Count(d => d.StartsWith("virtio-serial-pci,id=wq-qga-bus", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("rapport.pdf", 2, "rapport (2).pdf")]
    [InlineData("Photos", 3, "Photos (3)")]
    [InlineData(".bashrc", 2, ".bashrc (2)")]
    [InlineData("archive.tar.gz", 2, "archive.tar (2).gz")]
    public void A_name_already_taken_is_numbered_as_Explorer_does(string name, int number, string expected)
    {
        Assert.Equal(expected, GuestFileDrop.Numbered(name, number));
    }

    [Fact]
    public void A_folder_comes_before_its_contents_and_two_items_of_the_same_name_are_told_apart()
    {
        var one = Directory.CreateDirectory(Path.Combine(_folder, "one", "Photos")).FullName;
        File.WriteAllText(Path.Combine(one, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(one, "vide"));
        var other = Path.Combine(_folder, "two", "Photos");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "a file named like the folder");

        var entries = GuestFileDrop.Collect(new[] { one + Path.DirectorySeparatorChar, other });

        Assert.Equal(new[] { "Photos", "Photos/b.txt", "Photos/vide", "Photos (2)" }, entries.Select(e => e.RelativePath));
        Assert.Equal(new[] { true, false, true, false }, entries.Select(e => e.IsDirectory));
        Assert.Equal(1, entries[1].Length);
    }

    [Fact]
    public void A_missing_item_is_named()
    {
        var missing = Path.Combine(_folder, "absent.txt");
        var error = Assert.Throws<GuestFileDropException>(() => GuestFileDrop.Collect(new[] { missing }));
        Assert.Contains("absent.txt", error.Message);
    }

    [Theory]
    [InlineData("#nofolder /home/steph/Bureau", "le dossier /home/steph/Bureau est introuvable")]
    [InlineData("#badname", "nom invalide")]
    [InlineData("#badname a/../b", "nom invalide : a/../b")]
    [InlineData("#notfound C:\\Users\\x y", "C:\\Users\\x y : introuvable")]
    [InlineData("#nosession", "aucune session n'est ouverte dans l'invité : ouvrez une session Windows puis recommencez")]
    [InlineData("#nofolder", "#nofolder")]
    [InlineData("#unknown x", "#unknown x")]
    [InlineData("Accès refusé.", "Accès refusé.")]
    public void The_guests_reasons_are_said_in_the_interfaces_language(string sent, string shown)
    {
        Assert.Equal(shown, GuestFileDrop.GuestMessage(sent));
    }

    [Fact]
    public void The_linux_receiver_is_valid_perl()
    {
        var perl = FindPerl();
        if (perl is null)
            return;
        var script = Path.Combine(_folder, "receiver.pl");
        File.WriteAllText(script, GuestFileDrop.LinuxReceiver);
        var check = Process.Start(new ProcessStartInfo(perl, $"-c \"{script}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var output = check.StandardError.ReadToEnd();
        check.WaitForExit();
        Assert.True(check.ExitCode == 0, output);
    }

    [Fact]
    public async Task The_windows_receiver_writes_the_drop_and_numbers_names_already_taken()
    {
        var source = Directory.CreateDirectory(Path.Combine(_folder, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "nouveau");
        var photos = Directory.CreateDirectory(Path.Combine(source, "Photos", "été")).FullName;
        var picture = new byte[GuestFileDrop.ChunkSize * 2 + 12345];
        new Random(7).NextBytes(picture);
        File.WriteAllBytes(Path.Combine(photos, "b.bin"), picture);
        File.SetLastWriteTimeUtc(Path.Combine(photos, "b.bin"), new DateTime(2021, 5, 4, 3, 2, 1, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(source, "Photos", "vide"));
        var desktop = Directory.CreateDirectory(Path.Combine(_folder, "desktop")).FullName;
        File.WriteAllText(Path.Combine(desktop, "a.txt"), "ancien");

        var entries = GuestFileDrop.Collect(new[] { Path.Combine(source, "a.txt"), Path.Combine(source, "Photos") });
        var sent = new List<long>();
        var result = await RunWindowsReceiverAsync(desktop, entries, new Progress(p => sent.Add(p.BytesSent)), CancellationToken.None);

        Assert.Equal(desktop, result.GuestFolder);
        Assert.Equal(new[] { "a (2).txt", "Photos" }, result.Names);
        Assert.Equal("ancien", File.ReadAllText(Path.Combine(desktop, "a.txt")));
        Assert.Equal("nouveau", File.ReadAllText(Path.Combine(desktop, "a (2).txt")));
        var copy = Path.Combine(desktop, "Photos", "été", "b.bin");
        Assert.Equal(picture, File.ReadAllBytes(copy));
        Assert.Equal(new DateTime(2021, 5, 4, 3, 2, 1, DateTimeKind.Utc), File.GetLastWriteTimeUtc(copy));
        Assert.True(Directory.Exists(Path.Combine(desktop, "Photos", "vide")));
        Assert.Equal(picture.Length + "nouveau".Length, sent[^1]);
    }

    [Fact]
    public async Task A_cancelled_drop_leaves_nothing_behind_in_the_guest()
    {
        var source = Directory.CreateDirectory(Path.Combine(_folder, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "first.txt"), "complete");
        File.WriteAllBytes(Path.Combine(source, "second.bin"), new byte[GuestFileDrop.ChunkSize * 8]);
        var desktop = Directory.CreateDirectory(Path.Combine(_folder, "desktop")).FullName;

        var entries = GuestFileDrop.Collect(new[] { Path.Combine(source, "first.txt"), Path.Combine(source, "second.bin") });
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunWindowsReceiverAsync(desktop, entries,
            new Progress(p =>
            {
                if (p.BytesSent > GuestFileDrop.ChunkSize)
                    cancel.Cancel();
            }), cancel.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(desktop));
    }

    [Fact]
    public async Task A_process_file_left_full_of_zero_bytes_does_not_stop_the_next_drop()
    {
        // What a guest switched off as the file was written leaves: every later drop stopped on it.
        var work = Directory.CreateDirectory(Path.Combine(_folder, "work")).FullName;
        File.WriteAllBytes(Path.Combine(work, "drop.pid"), new byte[64]);
        var source = Directory.CreateDirectory(Path.Combine(_folder, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        var desktop = Directory.CreateDirectory(Path.Combine(_folder, "desktop")).FullName;

        var result = await RunWindowsReceiverAsync(desktop, GuestFileDrop.Collect(new[] { Path.Combine(source, "a.txt") }),
            new Progress(_ => { }), CancellationToken.None);

        Assert.Equal(new[] { "a.txt" }, result.Names);
    }

    /// <summary>
    /// Runs the receiver as a guest's agent would (same script, same standard input), on a named pipe:
    /// CreateFile opens one just as it opens the guest's port.
    /// </summary>
    private async Task<DropResult> RunWindowsReceiverAsync(string desktop, IReadOnlyList<DropEntry> entries,
        IProgress<DropProgress> progress, CancellationToken cancellationToken)
    {
        var name = "winqemu-drop-test-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var script = GuestFileDrop.WindowsReceiverScript($@"\\.\pipe\{name}", desktop, Path.Combine(_folder, "work"));
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
        using var receiver = Process.Start(start)!;
        var errors = receiver.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = receiver.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await receiver.StandardInput.WriteAsync(GuestFileDrop.WindowsReceiverInput);
        receiver.StandardInput.Close();

        using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var waiting = pipe.WaitForConnectionAsync(connect.Token);
            if (await Task.WhenAny(waiting, receiver.WaitForExitAsync(connect.Token)) != waiting)
                throw new Xunit.Sdk.XunitException("The receiver ended before opening the pipe: " + await errors);
            await waiting;
        }

        try
        {
            return await GuestFileDrop.TransferAsync(pipe, entries,
                async _ => receiver.HasExited ? $"code {receiver.ExitCode} {await errors}" : null, progress, cancellationToken);
        }
        finally
        {
            if (!receiver.WaitForExit(30000))
                receiver.Kill();
        }
    }

    private static string? FindPerl()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                     .Append(@"C:\Program Files\Git\usr\bin"))
        {
            var candidate = Path.Combine(folder.Trim('"'), "perl.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Reports at once, on the sending thread (System.Progress posts to a context, too late to cancel).</summary>
    private sealed class Progress : IProgress<DropProgress>
    {
        private readonly Action<DropProgress> _report;

        public Progress(Action<DropProgress> report) => _report = report;

        public void Report(DropProgress value) => _report(value);
    }
}
