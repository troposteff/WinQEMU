using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinQEMU.Core.Text;

namespace WinQEMU.Core.Tests;

/// <summary>
/// The interface's text: every English entry has its French one and the other way round, with the same
/// placeholders ({0}…), so neither language shows a key, a missing argument or a FormatException.
/// </summary>
public sealed class TextTests
{
    /// <summary>
    /// The tests read messages in French, as they were written: pinned here so that they pass whatever
    /// Windows' language on the machine running them.
    /// </summary>
    [ModuleInitializer]
    internal static void PinFrench()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
    }

    public static IEnumerable<object[]> TextFiles()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CLAUDE.md")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("repository root");
        foreach (var project in new[] { "WinQEMU.Core", "WinQEMU" })
            foreach (var file in Directory.GetFiles(Path.Combine(root, project, "Text"), "*.resx").Where(f => !f.EndsWith(".fr.resx", StringComparison.Ordinal)))
                yield return new object[] { Path.GetRelativePath(root, file), file };
    }

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+[^}]*\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [MemberData(nameof(TextFiles))]
    public void Every_entry_is_in_both_languages_with_the_same_placeholders(string name, string english)
    {
        var en = Read(english);
        var fr = Read(english.Replace(".resx", ".fr.resx", StringComparison.Ordinal));

        Assert.True(!en.Keys.Except(fr.Keys).Any(), $"{name}: no French for {string.Join(", ", en.Keys.Except(fr.Keys))}");
        Assert.True(!fr.Keys.Except(en.Keys).Any(), $"{name}: French only for {string.Join(", ", fr.Keys.Except(en.Keys))}");
        foreach (var (key, value) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{name}: {key} is empty in English");
            Assert.False(string.IsNullOrWhiteSpace(fr[key]), $"{name}: {key} is empty in French");
            Assert.True(Placeholders(value).SequenceEqual(Placeholders(fr[key])), $"{name}: {key} has different placeholders");
        }
    }

    [Fact]
    public void Sizes_read_in_the_interfaces_language()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.EndsWith(" GB", Sizes.Format(2.5 * 1024 * 1024 * 1024));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.EndsWith(" Go", Sizes.Format(2.5 * 1024 * 1024 * 1024));
            Assert.EndsWith(" Mo", Sizes.Format(3 * 1024 * 1024));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
