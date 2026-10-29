using WinQEMU.Core.Services;
using Xunit;

namespace WinQEMU.Core.Tests
{
    public sealed class SettingsImportTests : IDisposable
    {
        private const string StoreFamily = "12345troposteff.WinQEMU_abcdefghijklm";
        private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "wq-import-" + Guid.NewGuid().ToString("N"));

        private string Source => Path.Combine(_localAppData, "Packages", SettingsImport.SideloadedFamilyName,
            "LocalCache", "Local", "WinQEMU", "settings.json");

        private string Target => Path.Combine(_localAppData, "store", "WinQEMU", "settings.json");

        public void Dispose()
        {
            if (Directory.Exists(_localAppData))
                Directory.Delete(_localAppData, recursive: true);
        }

        private void WriteSource(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
            File.WriteAllText(Source, text);
        }

        [Fact]
        public void CopiesTheGitHubInstallationsSettingsAtFirstStart()
        {
            WriteSource("{\"LibraryRoot\":\"C:\\\\qemu\"}");

            Assert.True(SettingsImport.Import(_localAppData, StoreFamily, Target));
            Assert.Equal(File.ReadAllText(Source), File.ReadAllText(Target));
            Assert.True(File.Exists(Source));   // copied, not moved: the GitHub installation keeps working
        }

        [Fact]
        public void NeverOverwritesSettingsAlreadyThere()
        {
            WriteSource("{\"LibraryRoot\":\"C:\\\\old\"}");
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.WriteAllText(Target, "{\"LibraryRoot\":\"D:\\\\mine\"}");

            Assert.False(SettingsImport.Import(_localAppData, StoreFamily, Target));
            Assert.Contains("mine", File.ReadAllText(Target));
        }

        [Fact]
        public void LeavesTheGitHubInstallationAndUnpackagedRunsAlone()
        {
            WriteSource("{}");

            Assert.False(SettingsImport.Import(_localAppData, SettingsImport.SideloadedFamilyName, Target));
            Assert.False(SettingsImport.Import(_localAppData, null, Target));
            Assert.False(File.Exists(Target));
        }

        [Fact]
        public void DoesNothingWithoutAGitHubInstallation()
        {
            Assert.False(SettingsImport.Import(_localAppData, StoreFamily, Target));
            Assert.False(File.Exists(Target));
        }
    }
}
