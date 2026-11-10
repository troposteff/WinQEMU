using System.Diagnostics;
using System.Text;
using WinQEMU.Core.Sharing;
using Xunit;

namespace WinQEMU.Core.Tests;

public sealed class GuestDragOutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "winqemu-dragout-" + Guid.NewGuid().ToString("N"));

    public GuestDragOutTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void ADragGivesItsFiles()
    {
        var files = GuestDragOut.Parse("winqemu-dragout 1\ndrag\nfile C:\\Users\\alex\\Desktop\\a b.txt\nfile C:\\Users\\alex\\Desktop\\Photos\nend\n");
        Assert.Equal(new[] { @"C:\Users\alex\Desktop\a b.txt", @"C:\Users\alex\Desktop\Photos" }, files);
    }

    [Theory]
    [InlineData("winqemu-dragout 1\nidle\nend\n")]
    // Caught while the program writes it: the next read sees it whole.
    [InlineData("winqemu-dragout 1\ndrag\nfile C:\\a.txt\n")]
    [InlineData("winqemu-dragout 1\ndrag\nend\n")]
    [InlineData("")]
    [InlineData("winqemu-dragout 2\ndrag\nfile C:\\a.txt\nend\n")]
    public void AnythingElseIsNoDrag(string state) => Assert.Null(GuestDragOut.Parse(state));

    [Fact]
    public void WindowsLineEndingsAreRead() =>
        Assert.Equal(new[] { @"C:\a.txt" }, GuestDragOut.Parse("winqemu-dragout 1\r\ndrag\r\nfile C:\\a.txt\r\nend\r\n"));

    [Fact]
    public void TheSetupCarriesTheSourceAndItsName()
    {
        var script = GuestDragOut.SetupScript;
        Assert.Contains(GuestDragOut.ExecutableName, script);
        var encoded = script.Split('\'').First(part => part.Length > 1000);
        Assert.Equal(GuestDragOut.HelperSource, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.Matches("^WinQemuDragOut-[0-9a-f]{8}\\.exe$", GuestDragOut.ExecutableName);
    }

    /// <summary>The guest compiles it with Windows PowerShell's Add-Type (C# 5): so does this test.</summary>
    [Fact]
    public void TheProgramCompilesWithTheNetFramework()
    {
        var source = Path.Combine(_folder, "source.cs");
        var exe = Path.Combine(_folder, "dragout.exe");
        File.WriteAllText(source, GuestDragOut.HelperSource, new UTF8Encoding(false));
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"Add-Type -TypeDefinition ([IO.File]::ReadAllText('{source}')) -ReferencedAssemblies System.Windows.Forms, System.Drawing -OutputAssembly '{exe}' -OutputType WindowsApplication" })
            start.ArgumentList.Add(argument);
        using var compiler = Process.Start(start)!;
        var errors = compiler.StandardError.ReadToEnd();
        compiler.WaitForExit();
        Assert.True(compiler.ExitCode == 0 && File.Exists(exe), errors);
    }

    // ---- Linux ----

    [Fact]
    public void TheLinuxSetupCarriesTheProgramAndNoCarriageReturn()
    {
        var script = GuestDragOut.LinuxSetupScript;
        var encoded = script.Split('\'').First(part => part.Length > 1000);
        Assert.Equal(GuestDragOut.LinuxHelperSource, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.DoesNotContain('\r', script);
        Assert.DoesNotContain('\r', GuestDragOut.LinuxHelperSource);
        Assert.Contains(GuestDragOut.LinuxStatePath, script);
        Assert.Contains(GuestDragOut.LinuxEdgePath, script);
        // pkill's pattern must not match the setup script's own command line, which names the program too.
        Assert.Contains("'^[^ ]*python3 /usr/local/lib/winqemu/winqemu-dragout[.]py$'", script);
        // Under KWin the strip is a managed dock that says where it goes: an override-redirect window gets no drag there.
        Assert.Contains("_NET_WM_WINDOW_TYPE_DOCK", GuestDragOut.LinuxHelperSource);
        Assert.Contains("b'KWin' in name", GuestDragOut.LinuxHelperSource);
        // Whoever logs in later, the program can write its list: the setup does not wait for a session.
        Assert.Contains("chmod 666 /var/lib/winqemu/dragout.txt", script);
        // openSUSE's agent runs with UMask=0077: what the session reads is made readable whatever it was.
        Assert.Contains("umask 022", script);
        Assert.Contains("chmod 755 /usr/local/lib/winqemu", script);
        Assert.Contains("chmod 644 /etc/xdg/autostart/winqemu-dragout.desktop", script);
        Assert.DoesNotContain("exit 4", script);
    }

    /// <summary>The setup as /bin/sh reads it (Git's sh here; skipped where there is none).</summary>
    [Fact]
    public void TheLinuxSetupIsValidShell()
    {
        if (Tool("sh.exe") is not { } shell)
            return;
        var script = Path.Combine(_folder, "setup.sh");
        File.WriteAllText(script, GuestDragOut.LinuxSetupScript, new UTF8Encoding(false));
        var (code, output) = Run(shell, "-n", script);
        Assert.True(code == 0, output);
    }

    /// <summary>The program's file list from the text/uri-list it is given, run by Python (skipped where there is none).</summary>
    [Fact]
    public void TheLinuxProgramReadsFileUrisOnly()
    {
        if (Tool("python.exe") is not { } python)
            return;
        var program = Path.Combine(_folder, "dragout.py");
        File.WriteAllText(program, GuestDragOut.LinuxHelperSource, new UTF8Encoding(false));
        var check = Path.Combine(_folder, "check.py");
        // fcntl is Linux's only: a stand-in, the test never reaches main().
        File.WriteAllText(check, """
            import importlib.util, sys, types
            sys.modules['fcntl'] = types.ModuleType('fcntl')
            spec = importlib.util.spec_from_file_location('dragout', sys.argv[1])
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            listed = 'file:///home/alex/Bureau/a%20b.txt\r\n# comment\r\nhttps://example.org/x\r\nfile:///home/alex/%C3%A9t%C3%A9\r\n'
            print('|'.join(module.paths(listed)))
            """, new UTF8Encoding(false));
        var (code, output) = Run(python, "-I", "-X", "utf8", check, program);
        Assert.True(code == 0, output);
        Assert.Equal("/home/alex/Bureau/a b.txt|/home/alex/été", output.Trim());
    }

    private static string? Tool(string name) =>
        new[] { Path.Combine(@"C:\Program Files\Git\usr\bin", name) }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(folder => Path.Combine(folder.Trim('"'), name)))
            .FirstOrDefault(path => File.Exists(path) && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));

    private static (int Code, string Output) Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }
}
