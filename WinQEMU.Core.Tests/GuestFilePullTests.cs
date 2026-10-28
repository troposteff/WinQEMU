using System.Diagnostics;
using System.IO.Pipes;
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
        var script = GuestFilePull.WindowsSenderScript($@"\\.\pipe\{name}", paths, Path.Combine(_folder, "work"));
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
                async _ => sender.HasExited ? $"code {sender.ExitCode} {await errors}" : null, cancellationToken);
        }
        finally
        {
            // Cancelled, the sender is left writing to a pipe nobody reads: as in a guest, it is stopped.
            pipe.Dispose();
            if (!sender.WaitForExit(10000))
                sender.Kill();
        }
    }

    private sealed class Progress : IProgress<DropProgress>
    {
        private readonly Action<DropProgress> _report;

        public Progress(Action<DropProgress> report) => _report = report;

        public void Report(DropProgress value) => _report(value);
    }
}
