using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using WinQEMU.Core.Services;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Networking
{
    /// <summary>
    /// Installs OpenVPN's TAP-Windows driver, bundled with WinQEMU, and adds adapters to it: an internal
    /// network needs one adapter of its own.
    /// </summary>
    /// <remarks>
    /// Bundled at the user's request, unlike QEMU, which WinQEMU only locates: the GitHub releases of
    /// tap-windows6 now only ship merge modules (.msm), and installing the driver by hand was the one step
    /// of an internal network left to the user. The standalone installer (9.24.7, signed by OpenVPN Inc.)
    /// installs silently with /S, creates a first adapter, and leaves tapinstall.exe for more.
    /// </remarks>
    public static class TapDriver
    {
        // Renamed from tap-windows-9.24.7-I601-Win10.exe: the dots made the resource indexer read "7-I601-Win10"
        // as a qualifier (warning PRI249). The version is recorded in Assets\Drivers\README.txt.
        public const string InstallerFileName = "tap-windows-setup.exe";

        /// <summary>Where the bundled installer is: next to the application, under Assets\Drivers.</summary>
        public static string InstallerPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Drivers", InstallerFileName);

        /// <summary>The driver's install folder, as its installer records it; null when it is not installed.</summary>
        public static string? InstallDirectory
        {
            get
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\TAP-Windows");
                return key?.GetValue(null) is string folder && File.Exists(Path.Combine(folder, "bin", "tapinstall.exe")) ? folder : null;
            }
        }

        public static bool IsInstalled => InstallDirectory is not null || TapAdapter.List().Count > 0;

        /// <summary>
        /// Installs the driver and its first adapter. One UAC prompt; Windows may also ask whether to trust
        /// OpenVPN as a driver publisher. OperationCanceledException when declined.
        /// </summary>
        public static async Task InstallAsync(CancellationToken cancellationToken = default)
        {
            RequireGenuine(InstallerPath);
            var exitCode = await Elevation.RunAsync(InstallerPath, "/S", cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, PlatformText.TapInstallFailed, exitCode));
        }

        /// <summary>Adds an adapter with the driver's own tapinstall.exe. One UAC prompt.</summary>
        public static async Task AddAdapterAsync(CancellationToken cancellationToken = default)
        {
            var folder = InstallDirectory ?? throw new InvalidOperationException(PlatformText.TapDriverNotInstalled);
            var tool = Path.Combine(folder, "bin", "tapinstall.exe");
            RequireGenuine(tool);
            var exitCode = await Elevation.RunAsync(tool,
                $"install \"{Path.Combine(folder, "driver", "OemVista.inf")}\" tap0901", cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, PlatformText.TapAddAdapterFailed, exitCode));
        }

        /// <summary>
        /// The first adapter that was not in <paramref name="before"/>, once Windows has finished setting it
        /// up; null after <paramref name="timeout"/>.
        /// </summary>
        public static async Task<TapAdapter?> WaitForNewAdapterAsync(IReadOnlyCollection<string> before, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                var added = TapAdapter.List().FirstOrDefault(a => !before.Contains(a.Id, StringComparer.OrdinalIgnoreCase));
                if (added is not null)
                    return added;
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>
        /// Refuses to run a program as administrator unless Windows vouches for its signature and it is
        /// OpenVPN's: a file swapped in the package or the install folder must not get elevated.
        /// </summary>
        public static void RequireGenuine(string path)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, PlatformText.TapFileNotFound, Path.GetFileName(path), path));
            if (!IsTrusted(path))
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, PlatformText.TapSignatureInvalid, Path.GetFileName(path)));

#pragma warning disable SYSLIB0057   // the signer's certificate of a signed file, which X509CertificateLoader does not read
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!signer.Subject.Contains("O=OpenVPN Inc.", StringComparison.Ordinal))
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, PlatformText.TapNotSignedByOpenVpn, Path.GetFileName(path)));
        }

        /// <summary>WinVerifyTrust with the Authenticode policy: a valid, unrevoked signature, chained to a trusted root.</summary>
        private static bool IsTrusted(string path)
        {
            var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
            var fileHandle = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(file, fileHandle, false);
                var data = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,              // WTD_UI_NONE
                    RevocationChecks = 0,      // WTD_REVOKE_NONE: no network round trip for an offline installer
                    UnionChoice = 1,           // WTD_CHOICE_FILE
                    File = fileHandle,
                    StateAction = 0
                };
                var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(fileHandle);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            public string FilePath;
            public IntPtr File;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    }
}
