using System.Globalization;

namespace WinQEMU.Core.Text
{
    /// <summary>
    /// The interface's language: English, or French where Windows is in French, unless the user chose one in the
    /// settings (AppSettings.Language). Applied once, before any window, so it takes effect at the next start.
    /// </summary>
    /// <remarks>
    /// The text lives in Text\*.resx (English) with Text\*.fr.resx beside each; any other Windows language gets
    /// English. Only the interface's language is set: numbers and dates keep Windows' regional format.
    /// </remarks>
    public static class UiLanguage
    {
        /// <summary>The choices offered, "" being Windows' own language.</summary>
        public static readonly IReadOnlyList<string> Codes = new[] { "", "en", "fr" };

        /// <summary>Sets the language of every thread from here on; "" or an unknown code leaves Windows' one.</summary>
        public static void Apply(string? code)
        {
            var culture = code switch
            {
                "en" => CultureInfo.GetCultureInfo("en-US"),
                "fr" => CultureInfo.GetCultureInfo("fr-FR"),
                _ => null
            };
            if (culture is null)
                return;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        /// <summary>Whether the interface is in French (what is created in a guest follows it: ~/Partages or ~/Shares).</summary>
        public static bool IsFrench => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
    }
}
