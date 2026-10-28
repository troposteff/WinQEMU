using System.Text;
using System.Xml.Linq;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>The unattended Windows installation: the answer file and the names that go into it.</summary>
public class WindowsUnattendedSetupTests
{
    private static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";

    private static UnattendedWindows Settings(string user = "Stéphane", string password = "s3cr<et>&'") =>
        new(user, password, "WIN11-PRO", "fr-FR", "fr-FR", "040c:0000040c", "Romance Standard Time", "w11");

    private static XDocument Answer(UnattendedWindows? settings = null) =>
        XDocument.Parse(WindowsUnattendedSetup.AnswerFile(settings ?? Settings()));

    private static XElement Pass(XDocument document, string pass) =>
        document.Root!.Elements(U + "settings").Single(s => s.Attribute("pass")!.Value == pass);

    [Fact]
    public void The_answer_file_has_the_three_passes_Setup_needs_to_ask_nothing()
    {
        var document = Answer();

        Assert.Equal(new[] { "windowsPE", "specialize", "oobeSystem" },
            document.Root!.Elements(U + "settings").Select(s => s.Attribute("pass")!.Value));
        Assert.Equal("fr-FR", Pass(document, "windowsPE").Descendants(U + "SetupUILanguage").Single().Element(U + "UILanguage")!.Value);
        Assert.Equal("true", Pass(document, "windowsPE").Descendants(U + "AcceptEula").Single().Value);
        Assert.Equal(WindowsUnattendedSetup.GenericProKey, Pass(document, "windowsPE").Descendants(U + "Key").Single().Value);
        Assert.Equal("true", Pass(document, "windowsPE").Descendants(U + "WillWipeDisk").Single().Value);
        Assert.Equal("WIN11-PRO", Pass(document, "specialize").Descendants(U + "ComputerName").Single().Value);
        Assert.Equal("Romance Standard Time", Pass(document, "specialize").Descendants(U + "TimeZone").Single().Value);
    }

    [Fact]
    public void Windows_11s_hardware_check_is_still_lifted()
    {
        var paths = Pass(Answer(), "windowsPE").Descendants(U + "Path").Select(p => p.Value).ToList();

        Assert.Contains(@"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f", paths);
        Assert.Contains(@"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f", paths);
    }

    [Fact]
    public void The_account_is_made_an_administrator_by_the_groups_SID_not_its_translated_name()
    {
        var script = WindowsUnattendedSetup.AccountScript(Settings());

        Assert.Contains("Add-LocalGroupMember -SID S-1-5-32-544 -Member 'Stéphane'", script);
        Assert.DoesNotContain("s3cr<et>", script);
        Assert.Contains(Convert.ToBase64String(Encoding.Unicode.GetBytes("s3cr<et>&'")), script);
    }

    [Fact]
    public void A_quote_in_the_user_name_cannot_break_out_of_the_script()
    {
        var script = WindowsUnattendedSetup.AccountScript(Settings(user: "O'Neil"));

        Assert.Contains("-Member 'O''Neil'", script);
    }

    [Fact]
    public void Auto_logon_carries_the_password_as_Setup_encodes_it()
    {
        var logon = Pass(Answer(), "oobeSystem").Descendants(U + "AutoLogon").Single();
        var value = logon.Element(U + "Password")!.Element(U + "Value")!.Value;

        Assert.Equal("s3cr<et>&'Password", Encoding.Unicode.GetString(Convert.FromBase64String(value)));
        Assert.Equal("false", logon.Element(U + "Password")!.Element(U + "PlainText")!.Value);
        Assert.Equal("1", logon.Element(U + "LogonCount")!.Value);
    }

    [Fact]
    public void An_empty_password_makes_an_account_without_one()
    {
        var script = WindowsUnattendedSetup.AccountScript(Settings(password: string.Empty));

        Assert.Contains("-NoPassword", script);
    }

    [Fact]
    public void Every_command_stays_within_what_Setup_accepts()
    {
        // A 1694-character RunSynchronous path made Setup stop at "L'ordinateur a redémarré de manière inattendue".
        var document = Answer(Settings(user: "un-nom-de-vingt-car", password: new string('x', 127)));

        Assert.All(document.Descendants(U + "Path"), path => Assert.True(path.Value.Length < 260, path.Value));
        Assert.All(document.Descendants(U + "CommandLine"), line => Assert.True(line.Value.Length < 1024, line.Value));
    }

    [Fact]
    public void The_account_is_created_by_its_script_from_the_setup_stick()
    {
        var command = Pass(Answer(), "specialize").Descendants(U + "Path").First().Value;

        Assert.Contains(@"if exist %d:\WQUSER.PS1", command);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.Unicode.GetBytes("s3cr<et>&'")), command);
    }

    [Fact]
    public void The_first_logon_runs_the_script_from_the_setup_stick()
    {
        var command = Pass(Answer(), "oobeSystem").Descendants(U + "CommandLine").Single().Value;

        Assert.Contains(@"if exist %d:\WQSETUP.PS1", command);
        Assert.True(command.Length < 1024, "Windows Setup ignores longer command lines");
    }

    [Theory]
    [InlineData("Windows 11", "WINDOWS-11")]
    [InlineData("Poste de développement", "POSTE-DE-DEVELO")]
    [InlineData("  été ", "ETE")]
    [InlineData("2026", "WINQEMU-VM")]
    [InlineData("***", "WINQEMU-VM")]
    public void A_computer_name_is_made_from_the_machines_name(string machine, string expected)
    {
        Assert.Equal(expected, WindowsUnattendedSetup.ComputerNameFor(machine));
    }

    [Theory]
    [InlineData("Stéphane", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("Administrateur", false)]
    [InlineData("un-nom-bien-trop-long-pour-windows", false)]
    public void User_names_follow_Windows_rules(string name, bool accepted)
    {
        Assert.Equal(accepted, WindowsUnattendedSetup.WhyUserNameIsRefused(name) is null);
    }
}
