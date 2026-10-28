using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace WinQEMU.Core.Services
{
    /// <summary>What an unattended Windows installation needs to know, asked in the creation wizard.</summary>
    /// <param name="Language">Setup's language, which must be one the ISO holds ("fr-FR").</param>
    /// <param name="Locale">Formats and system locale, from this PC ("fr-FR").</param>
    /// <param name="InputLocale">Keyboard, from this PC ("040c:0000040c").</param>
    /// <param name="TimeZone">Windows time zone id, from this PC ("Romance Standard Time").</param>
    /// <param name="DriverFolder">virtio-win's folder for this Windows ("w11", "w10").</param>
    public sealed record UnattendedWindows(
        string UserName,
        string Password,
        string ComputerName,
        string Language,
        string Locale,
        string InputLocale,
        string TimeZone,
        string DriverFolder);

    /// <summary>
    /// A complete answer file for Windows Setup, as VMware's "installation simplifiée" writes one: the user
    /// types a name and a password in the wizard, and Windows installs by itself, then installs virtio-win's
    /// drivers and tools and the guest agent at the first logon.
    /// </summary>
    /// <remarks>
    /// On the same read-only USB stick as the hardware-check answers (WindowsSetupAnswer), with the
    /// first-logon script beside it (an answer file's command line stops at 1024 characters).
    ///
    /// The account is created by a script in the specialize pass and put in the administrators group by
    /// its SID: the group's name is translated ("Administrateurs"), and an answer file naming a group
    /// that does not exist fails the installation. The edition comes from Microsoft's published generic
    /// key for Pro, which selects the edition and activates nothing. The password is only base64 in the
    /// file, as Microsoft's own "PlainText=false" is; the first-logon script deletes the copies Windows
    /// keeps in C:\Windows\Panther, and WinQEMU deletes the file once the guest agent answers.
    /// </remarks>
    public static class WindowsUnattendedSetup
    {
        public const string ScriptFileName = "WQSETUP.PS1";

        /// <summary>The account script, run in the specialize pass.</summary>
        public const string AccountScriptFileName = "WQUSER.PS1";

        /// <summary>
        /// A command that runs a script of the setup stick, whichever letter Windows gave it. Short on purpose:
        /// Setup refuses a RunSynchronous path over 259 characters ("la valeur n'est pas valide", then
        /// "L'ordinateur a redémarré de manière inattendue") — the account script, inline, made 1694.
        /// </summary>
        private static string FromStick(string script) =>
            $@"cmd /c for %d in (D E F G H I J K L M N) do @if exist %d:\{script} powershell -NoProfile -ExecutionPolicy Bypass -File %d:\{script}";

        /// <summary>Microsoft's generic installation key for Windows 10/11 Pro: picks the edition, activates nothing.</summary>
        public const string GenericProKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";

        private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";
        private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

        /// <summary>The settings for a new machine: language from the ISO, locale, keyboard and time zone from this PC.</summary>
        public static UnattendedWindows ForHost(string userName, string password, string machineName, string isoPath, string guestOs)
        {
            var locale = CultureInfo.CurrentCulture.Name;
            var language = IsoFileReader.WindowsLanguage(isoPath) is { } fromIso
                ? CultureInfo.GetCultureInfo(fromIso).Name
                : CultureInfo.CurrentUICulture.Name;
            return new UnattendedWindows(userName.Trim(), password, ComputerNameFor(machineName), language, locale,
                HostInputLocale(), TimeZoneInfo.Local.Id, guestOs == "windows-10" ? "w10" : "w11");
        }

        /// <summary>
        /// A valid Windows computer name from a machine name: letters, digits and hyphens, at most 15
        /// characters (NetBIOS), not only digits.
        /// </summary>
        public static string ComputerNameFor(string machineName)
        {
            var folded = new string(machineName.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '-')
                .ToArray());
            while (folded.Contains("--", StringComparison.Ordinal))
                folded = folded.Replace("--", "-", StringComparison.Ordinal);
            folded = folded.Trim('-');
            if (folded.Length > 15)
                folded = folded[..15].TrimEnd('-');
            return folded.Length == 0 || folded.All(char.IsAsciiDigit) ? "WINQEMU-VM" : folded;
        }

        /// <summary>Why a user name cannot be used, or null. Windows refuses these characters in account names.</summary>
        public static string? WhyUserNameIsRefused(string userName)
        {
            var name = userName.Trim();
            if (name.Length == 0)
                return "Indiquez un nom d'utilisateur.";
            if (name.Length > 20)
                return "Un nom d'utilisateur Windows fait au plus 20 caractères.";
            if (name.IndexOfAny("\"/\\[]:;|=,+*?<>@".ToCharArray()) >= 0 || name.EndsWith('.'))
                return "Un nom d'utilisateur Windows ne peut contenir aucun de ces caractères : \" / \\ [ ] : ; | = , + * ? < > @";
            if (name.Equals("Administrateur", StringComparison.OrdinalIgnoreCase) || name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Invité", StringComparison.OrdinalIgnoreCase) || name.Equals("Guest", StringComparison.OrdinalIgnoreCase))
                return $"« {name} » est un compte intégré de Windows : choisissez un autre nom.";
            return null;
        }

        /// <summary>Writes the answer file and the first-logon script into the setup stick's folder.</summary>
        public static void WriteTo(string folder, UnattendedWindows settings)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, WindowsSetupAnswer.FileName), AnswerFile(settings), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, ScriptFileName), FirstLogonScript, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(folder, AccountScriptFileName), AccountScript(settings), new UTF8Encoding(true));
        }

        /// <summary>
        /// Creates the account and makes it an administrator by the group's SID (see the remarks). The password
        /// travels base64, as everywhere else here.
        /// </summary>
        public static string AccountScript(UnattendedWindows s)
        {
            var user = s.UserName.Replace("'", "''", StringComparison.Ordinal);
            var createUser = s.Password.Length == 0
                ? $"New-LocalUser -Name '{user}' -NoPassword -AccountNeverExpires | Out-Null"
                : $"$p = ConvertTo-SecureString ([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.Unicode.GetBytes(s.Password))}'))) -AsPlainText -Force\r\n" +
                  $"New-LocalUser -Name '{user}' -Password $p -PasswordNeverExpires -AccountNeverExpires | Out-Null";
            return "# Written by WinQEMU: the account of an unattended installation.\r\n" +
                   $"{createUser}\r\nAdd-LocalGroupMember -SID S-1-5-32-544 -Member '{user}'\r\n";
        }

        /// <summary>Base64 of the UTF-16 password followed by the element's name, as Windows Setup reads "PlainText=false".</summary>
        public static string EncodedPassword(string password, string element) =>
            Convert.ToBase64String(Encoding.Unicode.GetBytes(password + element));

        public static string AnswerFile(UnattendedWindows s)
        {
            XElement Component(string name, params object[] content) => new(Unattend + "component",
                new XAttribute("name", name), new XAttribute("processorArchitecture", "amd64"),
                new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"), content);
            XElement E(string name, params object[] content) => new(Unattend + name, content);
            XElement Added(string name, params object[] content) => new(Unattend + name, new XAttribute(Wcm + "action", "add"), content);
            XElement International(string name, bool winPe) => Component(name,
                winPe ? E("SetupUILanguage", E("UILanguage", s.Language)) : null!,
                E("InputLocale", s.InputLocale), E("SystemLocale", s.Locale), E("UILanguage", s.Language), E("UserLocale", s.Locale));

            var labConfig = new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck", "BypassStorageCheck" };
            var windowsPe = new XElement(Unattend + "settings", new XAttribute("pass", "windowsPE"),
                International("Microsoft-Windows-International-Core-WinPE", winPe: true),
                // virtio-win's storage driver, for a system disk on virtio: the drive letter of its CD is not
                // known, so every likely one is listed; Setup skips those that do not exist.
                Component("Microsoft-Windows-PnpCustomizationsWinPE",
                    E("DriverPaths", "DEFGHIJ".Select((letter, i) => Added("PathAndCredentials",
                        new XAttribute(Wcm + "keyValue", (i + 1).ToString(CultureInfo.InvariantCulture)),
                        E("Path", $@"{letter}:\viostor\{s.DriverFolder}\amd64"))))),
                Component("Microsoft-Windows-Setup",
                    E("RunSynchronous", labConfig.Select((check, i) => Added("RunSynchronousCommand",
                        E("Order", (i + 1).ToString(CultureInfo.InvariantCulture)),
                        E("Path", $@"reg add HKLM\SYSTEM\Setup\LabConfig /v {check} /t REG_DWORD /d 1 /f")))),
                    E("DiskConfiguration", Added("Disk",
                        E("DiskID", "0"), E("WillWipeDisk", "true"),
                        E("CreatePartitions",
                            Added("CreatePartition", E("Order", "1"), E("Type", "EFI"), E("Size", "300")),
                            Added("CreatePartition", E("Order", "2"), E("Type", "MSR"), E("Size", "16")),
                            Added("CreatePartition", E("Order", "3"), E("Type", "Primary"), E("Extend", "true"))),
                        E("ModifyPartitions",
                            Added("ModifyPartition", E("Order", "1"), E("PartitionID", "1"), E("Format", "FAT32"), E("Label", "System")),
                            Added("ModifyPartition", E("Order", "2"), E("PartitionID", "2")),
                            Added("ModifyPartition", E("Order", "3"), E("PartitionID", "3"), E("Format", "NTFS"), E("Label", "Windows"), E("Letter", "C"))))),
                    E("ImageInstall", E("OSImage", E("InstallTo", E("DiskID", "0"), E("PartitionID", "3")))),
                    E("UserData", E("AcceptEula", "true"), E("ProductKey", E("Key", GenericProKey), E("WillShowUI", "OnError")))));

            var specialize = new XElement(Unattend + "settings", new XAttribute("pass", "specialize"),
                Component("Microsoft-Windows-Shell-Setup", E("ComputerName", s.ComputerName), E("TimeZone", s.TimeZone)),
                Component("Microsoft-Windows-Deployment", E("RunSynchronous",
                    Added("RunSynchronousCommand", E("Order", "1"), E("Path", FromStick(AccountScriptFileName))),
                    // OOBE would otherwise insist on a network for a Microsoft account.
                    Added("RunSynchronousCommand", E("Order", "2"),
                        E("Path", @"reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f")))));

            var oobe = new XElement(Unattend + "settings", new XAttribute("pass", "oobeSystem"),
                International("Microsoft-Windows-International-Core", winPe: false),
                Component("Microsoft-Windows-Shell-Setup",
                    E("OOBE",
                        E("HideEULAPage", "true"), E("HideOEMRegistrationScreen", "true"), E("HideOnlineAccountScreens", "true"),
                        E("HideLocalAccountScreen", "true"), E("HideWirelessSetupInOOBE", "true"), E("ProtectYourPC", "3")),
                    E("AutoLogon",
                        E("Username", s.UserName), E("Enabled", "true"), E("LogonCount", "1"),
                        E("Password", E("Value", EncodedPassword(s.Password, "Password")), E("PlainText", "false"))),
                    // The script sits on the setup stick, whose letter is not known: the first drive that has it.
                    E("FirstLogonCommands", Added("SynchronousCommand", E("Order", "1"), E("RequiresUserInput", "false"),
                        E("CommandLine", FromStick(ScriptFileName))))));

            var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XComment(" Written by WinQEMU for an unattended installation. "),
                new XElement(Unattend + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", Wcm), windowsPe, specialize, oobe));
            return document.Declaration + Environment.NewLine + document;
        }

        /// <summary>
        /// Run at the first logon, elevated: virtio-win's drivers and tools, then the guest agent, both from
        /// the virtio-win drive when there is one. Red Hat's publisher is trusted first, or every driver
        /// stops to ask "Installer ce logiciel de périphérique ?".
        /// </summary>
        public const string FirstLogonScript = """
            # Written by WinQEMU: completes an unattended installation at the first logon.
            $log = "$env:ProgramData\WinQEMU\setup.log"
            New-Item (Split-Path $log) -ItemType Directory -Force | Out-Null
            Start-Transcript -Path $log -Append | Out-Null
            # Windows keeps copies of the answer file, which hold the account's password.
            Remove-Item "$env:SystemRoot\Panther\unattend.xml", "$env:SystemRoot\Panther\unattend-original.xml" -Force -ErrorAction SilentlyContinue
            $drive = Get-PSDrive -PSProvider FileSystem | Where-Object { Test-Path (Join-Path $_.Root 'virtio-win-guest-tools.exe') } | Select-Object -First 1
            if ($drive) {
                $root = $drive.Root
                $catalog = Get-ChildItem (Join-Path $root 'Balloon') -Recurse -Filter *.cat -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($catalog) {
                    $certificate = (Get-AuthenticodeSignature $catalog.FullName).SignerCertificate
                    if ($certificate) {
                        $store = New-Object Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'LocalMachine')
                        $store.Open('ReadWrite'); $store.Add($certificate); $store.Close()
                    }
                }
                Start-Process (Join-Path $root 'virtio-win-guest-tools.exe') -ArgumentList '/install', '/quiet', '/norestart' -Wait
                $agent = Join-Path $root 'guest-agent\qemu-ga-x86_64.msi'
                if (Test-Path $agent) { Start-Process msiexec.exe -ArgumentList '/i', "`"$agent`"", '/qn', '/norestart' -Wait }
            } else {
                Write-Output 'No virtio-win drive: drivers and guest agent not installed.'
            }
            Stop-Transcript | Out-Null
            """;

        /// <summary>This PC's keyboard as Windows Setup writes it: "040c:0000040c" for French AZERTY.</summary>
        private static string HostInputLocale()
        {
            var name = new StringBuilder(9);
            if (OperatingSystem.IsWindows() && GetKeyboardLayoutName(name) && name.Length == 8)
            {
                var layout = name.ToString().ToLowerInvariant();
                return $"{layout[4..]}:{layout}";
            }
            return CultureInfo.CurrentCulture.Name;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetKeyboardLayoutName(StringBuilder name);
    }
}
