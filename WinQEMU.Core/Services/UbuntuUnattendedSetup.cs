using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>What an unattended Ubuntu installation needs to know.</summary>
    /// <param name="PasswordHash">SHA-512 crypt of the password: the clear one never goes into the file.</param>
    /// <param name="Locale">"fr_FR.UTF-8", from this PC.</param>
    /// <param name="KeyboardLayout">xkb layout ("fr"), from this PC's keyboard.</param>
    /// <param name="TimeZone">IANA time zone ("Europe/Paris"), from this PC's.</param>
    public sealed record UnattendedLinux(
        string UserName,
        string PasswordHash,
        string Hostname,
        string Locale,
        string KeyboardLayout,
        string TimeZone);

    /// <summary>
    /// Ubuntu's own unattended installation (autoinstall), as VMware's "installation simplifiée" does for
    /// Linux: the installer reads its answers from a drive labelled CIDATA, and installs the guest agent and
    /// spice-vdagent, so shared folders and the clipboard work from the first logon.
    /// </summary>
    /// <remarks>
    /// The installer only goes ahead on its own when "autoinstall" is on the kernel's command line; without
    /// it, it asks for confirmation. Rather than rewrite the ISO's boot menu, the first start boots the ISO's
    /// own kernel and initrd directly (QEMU's -kernel), read out of the ISO at creation; once the installer
    /// reboots, the machine starts from its disk. The answers sit on the same kind of read-only stick as
    /// Windows' (vvfat), labelled CIDATA, where cloud-init's NoCloud source looks.
    /// </remarks>
    public static class UbuntuUnattendedSetup
    {
        /// <summary>Folders and files under the machine's setup folder.</summary>
        public const string CidataFolder = "cidata";
        public const string KernelFile = "vmlinuz";
        public const string InitrdFile = "initrd";

        /// <summary>"autoinstall" for the installer alone, before "---"; what follows is the ISO's own line.</summary>
        public const string KernelArguments = "autoinstall --- quiet splash";

        public static UnattendedLinux ForHost(string userName, string password, string machineName)
        {
            var culture = CultureInfo.CurrentCulture.Name.Replace('-', '_');
            var timeZone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";
            return new UnattendedLinux(userName.Trim(), Sha512Crypt.Hash(password), HostnameFor(machineName),
                (culture.Length > 0 ? culture : "en_US") + ".UTF-8", HostKeyboardLayout(), timeZone);
        }

        /// <summary>A Linux host name from a machine name: lower-case letters, digits and hyphens, at most 63.</summary>
        public static string HostnameFor(string machineName)
        {
            var folded = new string(machineName.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
                .ToArray());
            folded = Regex.Replace(folded, "-{2,}", "-").Trim('-');
            if (folded.Length > 63)
                folded = folded[..63].TrimEnd('-');
            return folded.Length == 0 ? "ubuntu" : folded;
        }

        /// <summary>Why a user name cannot be used on Linux, or null.</summary>
        public static string? WhyUserNameIsRefused(string userName)
        {
            var name = userName.Trim();
            if (name.Length == 0)
                return CoreText.UnattendedUserNameRequired;
            if (!Regex.IsMatch(name, "^[a-z][a-z0-9_-]{0,31}$"))
                return CoreText.UnattendedLinuxUserNameRule;
            if (name is "root" or "daemon" or "bin" or "sys" or "nobody" or "admin" or "ubuntu")
                return string.Format(CultureInfo.CurrentCulture, CoreText.UnattendedLinuxReservedAccount, name);
            return null;
        }

        /// <summary>The installer's answers, as cloud-init user-data.</summary>
        public static string UserData(UnattendedLinux s) =>
            $"""
            #cloud-config
            # Written by WinQEMU for an unattended installation.
            autoinstall:
              version: 1
              locale: {Quote(s.Locale)}
              keyboard:
                layout: {Quote(s.KeyboardLayout)}
              timezone: {Quote(s.TimeZone)}
              identity:
                hostname: {Quote(s.Hostname)}
                realname: {Quote(s.UserName)}
                username: {Quote(s.UserName)}
                password: {Quote(s.PasswordHash)}
              storage:
                layout:
                  name: direct
              packages:
                - qemu-guest-agent
                - spice-vdagent
              shutdown: reboot

            """;

        /// <summary>
        /// Writes the CIDATA folder and the ISO's kernel and initrd into the setup folder. False when the ISO
        /// has no casper kernel: not an Ubuntu installation disc.
        /// </summary>
        public static bool WriteTo(string folder, UnattendedLinux settings, string isoPath)
        {
            var kernel = IsoFileReader.Read(isoPath, "casper/vmlinuz", 256 * 1024 * 1024);
            var initrd = IsoFileReader.Read(isoPath, "casper/initrd", 512 * 1024 * 1024);
            if (kernel is null || initrd is null)
                return false;

            var cidata = Path.Combine(folder, CidataFolder);
            Directory.CreateDirectory(cidata);
            File.WriteAllText(Path.Combine(cidata, "user-data"), UserData(settings).ReplaceLineEndings("\n"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(cidata, "meta-data"), $"instance-id: winqemu-{Guid.NewGuid():N}\n", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(folder, KernelFile), kernel);
            File.WriteAllBytes(Path.Combine(folder, InitrdFile), initrd);
            return true;
        }

        /// <summary>Whether an ISO is one this can install unattended: an Ubuntu disc with its casper kernel.</summary>
        public static bool CanInstall(string isoPath) =>
            File.Exists(isoPath) && IsoFileReader.Read(isoPath, "casper/vmlinuz", 256 * 1024 * 1024) is not null;

        /// <summary>A YAML double-quoted scalar.</summary>
        private static string Quote(string value) =>
            "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        /// <summary>This PC's keyboard as an xkb layout, from its Windows layout id; "us" for the others.</summary>
        private static string HostKeyboardLayout()
        {
            var name = new StringBuilder(9);
            if (!OperatingSystem.IsWindows() || !GetKeyboardLayoutName(name))
                return "us";
            return name.ToString().ToUpperInvariant() switch
            {
                "0000040C" => "fr",
                "0000080C" => "be",
                "0000100C" => "ch",
                "00000807" => "ch",
                "00000C0C" or "00001009" => "ca",
                "00000407" => "de",
                "00000809" => "gb",
                "0000040A" or "0000080A" => "es",
                "00000410" => "it",
                "00000816" => "pt",
                "00000416" => "br",
                "00000413" => "nl",
                _ => "us"
            };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetKeyboardLayoutName(StringBuilder name);
    }
}
