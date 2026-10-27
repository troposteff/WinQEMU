using System.Globalization;
using System.Text;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Makes a Windows guest's resolution follow the console, as VMware Tools do: Ubuntu follows the
    /// window by itself, Windows with virtio-win's display driver only takes the window's size once,
    /// when the driver loads at boot.
    /// </summary>
    /// <remarks>
    /// A resolution can only be set from the user's own session, never by the agent, which runs as
    /// SYSTEM in session 0. So the agent compiles a small helper with the .NET Framework that ships with
    /// Windows (nothing downloaded), and registers a task of the Users group that runs it; each change
    /// leaves the size in the registry and starts the task (about half a second through the agent).
    ///
    /// The driver offers a fixed list of standard modes (1280x720, 1280x800, 1440x900, 1600x1000,
    /// 1920x1080, 1920x1200…) plus the window's size at boot, and adds no new size afterwards: asked for
    /// 1400x800 it can only give 1280x800. The helper takes the exact size when offered, else the largest
    /// mode that fits, which the console then scales. Full screen on a 1920x1080 screen is exact.
    /// The change is not saved in Windows' display settings, which would outrank the window's size at
    /// the next boot.
    /// </remarks>
    public static class WindowsGuestDisplay
    {
        public const string TaskName = "WinQEMU-Resolution";

        /// <summary>The helper, run in the user's session: sets the size left in HKLM\SOFTWARE\WinQEMU\Resolution.</summary>
        public const string HelperSource = """
            using System;
            using System.Runtime.InteropServices;
            using Microsoft.Win32;

            static class SetResolution
            {
                [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
                struct DevMode
                {
                    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
                    public short SpecVersion, DriverVersion, Size, DriverExtra;
                    public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
                    public short Color, Duplex, YResolution, TTOption, Collate;
                    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
                    public short LogPixels;
                    public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency,
                        IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
                }

                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern int ChangeDisplaySettingsEx(string device, ref DevMode devMode, IntPtr window, int flags, IntPtr param);

                static int Main()
                {
                    var wanted = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WinQEMU", "Resolution", null) as string;
                    var parts = wanted == null ? new string[0] : wanted.Split('x');
                    int width, height;
                    if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
                        return 2;

                    var current = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
                    EnumDisplaySettings(null, -1, ref current);
                    DevMode best = current;
                    var found = false;
                    for (var i = 0; ; i++)
                    {
                        var mode = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
                        if (!EnumDisplaySettings(null, i, ref mode))
                            break;
                        if (mode.BitsPerPel != current.BitsPerPel || mode.PelsWidth > width || mode.PelsHeight > height)
                            continue;
                        if (!found || (long)mode.PelsWidth * mode.PelsHeight > (long)best.PelsWidth * best.PelsHeight)
                        {
                            best = mode;
                            found = true;
                        }
                    }
                    if (!found || (best.PelsWidth == current.PelsWidth && best.PelsHeight == current.PelsHeight))
                        return 0;

                    best.Fields = 0x80000 | 0x100000;   // DM_PELSWIDTH | DM_PELSHEIGHT
                    return ChangeDisplaySettingsEx(null, ref best, IntPtr.Zero, 0, IntPtr.Zero) == 0 ? 0 : 4;
                }
            }
            """;

        /// <summary>Run as SYSTEM by the agent: compiles the helper and registers its task.</summary>
        public static string SetupScript => """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $dir = "$env:ProgramData\WinQEMU"
            New-Item $dir -ItemType Directory -Force | Out-Null
            $source = @'
            """ + "\n" + HelperSource + "\n" + """
            '@
            Add-Type -TypeDefinition $source -OutputAssembly "$dir\SetResolution.exe" -OutputType WindowsApplication
            $principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
            Register-ScheduledTask -TaskName 'WinQEMU-Resolution' -Action (New-ScheduledTaskAction -Execute "$dir\SetResolution.exe") -Principal $principal -Force | Out-Null
            """;

        /// <summary>Compiles the helper and registers its task. The guest may still be booting: see <see cref="GuestShareMounter"/>.</summary>
        public static async Task SetupAsync(GuestAgentClient agent, CancellationToken cancellationToken = default)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(SetupScript));
            var result = await agent.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded },
                null, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new GuestAgentException($"The resolution helper could not be set up: {result.Error.Trim()}");
        }

        /// <summary>
        /// Asks for a resolution. Native programs rather than PowerShell, which takes a second to start; the
        /// task name has no space, as the agent quotes arguments on its own.
        /// </summary>
        public static async Task RequestAsync(GuestAgentClient agent, int width, int height, CancellationToken cancellationToken = default)
        {
            var size = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
            var result = await agent.RunAsync("cmd.exe",
                new[] { "/c", $@"reg add HKLM\SOFTWARE\WinQEMU /v Resolution /d {size} /f >nul && schtasks /run /tn {TaskName} >nul" },
                null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new GuestAgentException($"The resolution could not be asked for: {result.Error.Trim()}");
        }
    }
}
