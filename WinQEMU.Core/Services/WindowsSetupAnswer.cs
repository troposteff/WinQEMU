namespace WinQEMU.Core.Services
{
    /// <summary>
    /// The answer file that lets Windows 11 Setup run on a machine without TPM or Secure Boot, which WHPX
    /// cannot provide (no swtpm in QEMU's Windows builds, no SMM under WHPX). Without it, Setup stops at
    /// "Ce PC ne peut pas exécuter Windows 11", and the usual way round is Shift+F10 and regedit by hand.
    /// </summary>
    /// <remarks>
    /// Windows Setup looks for autounattend.xml at the root of every removable drive. This one only runs,
    /// in the windowsPE pass, the reg commands that set Microsoft's own LabConfig switches, before Setup
    /// checks the hardware; it answers nothing else, so Setup asks its usual questions (language, edition,
    /// disk, account). The drive is a read-only USB stick QEMU makes from a folder (vvfat): nothing to
    /// download, no image to build.
    /// </remarks>
    public static class WindowsSetupAnswer
    {
        public const string FileName = "autounattend.xml";

        private static readonly string[] Checks = { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck", "BypassStorageCheck" };

        public static string Content
        {
            get
            {
                var commands = string.Concat(Checks.Select((check, index) => $"""
                                <RunSynchronousCommand wcm:action="add">
                                    <Order>{index + 1}</Order>
                                    <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v {check} /t REG_DWORD /d 1 /f</Path>
                                </RunSynchronousCommand>

                """));
                return $"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <!-- Written by WinQEMU: lets Windows 11 Setup run without TPM and Secure Boot, which this hypervisor cannot provide. -->
                    <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
                        <settings pass="windowsPE">
                            <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                                <RunSynchronous>
                    {commands.TrimEnd()}
                                </RunSynchronous>
                            </component>
                        </settings>
                    </unattend>

                    """;
            }
        }

        /// <summary>Writes the answer file into <paramref name="folder"/>, the folder the setup stick shows.</summary>
        public static void WriteTo(string folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path) || File.ReadAllText(path) != Content)
                File.WriteAllText(path, Content);
        }
    }
}
