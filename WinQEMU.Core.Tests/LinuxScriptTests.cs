using WinQEMU.Core.Services;
using WinQEMU.Core.Sharing;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Scripts run by a Linux guest are raw string literals, which take the source file's line endings: in a
/// checkout with Windows ones, /bin/sh stopped on "set -u\r" ("set: Illegal option -") and no shared folder
/// was mounted. Each is made Unix-only where it is defined.
/// </summary>
public sealed class LinuxScriptTests
{
    public static IEnumerable<object[]> Scripts() => new[]
    {
        new object[] { nameof(GuestShareMounter.MountScript), GuestShareMounter.MountScript },
        new object[] { nameof(GuestTools.LinuxToolsScript), GuestTools.LinuxToolsScript },
        new object[] { nameof(GuestTools.LinuxCheckScript), GuestTools.LinuxCheckScript },
        new object[] { nameof(GuestFileDrop.LinuxReceiver), GuestFileDrop.LinuxReceiver },
        new object[] { nameof(GuestFilePull.LinuxSender), GuestFilePull.LinuxSender }
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void A_script_for_a_Linux_guest_has_no_carriage_return(string name, string script)
    {
        Assert.False(script.Contains('\r'), $"{name} holds a carriage return");
    }
}
