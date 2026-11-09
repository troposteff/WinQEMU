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
}
