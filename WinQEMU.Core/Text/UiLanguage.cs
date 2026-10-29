using System.Globalization;

namespace WinQEMU.Core.Text
{
    /// <summary>
    /// The interface's language: English, or French where Windows is in French, unless the user chose one in the
    /// settings (AppSettings.Language). Applied before any window, and again whenever the setting changes: the shell
    /// then refreshes what it shows (MainWindow.ApplyLanguage).
    /// </summary>
    /// <remarks>
    /// The text lives in Text\*.resx (English) with Text\*.fr.resx beside each; any other Windows language gets
    /// English. Only the interface's language is set: numbers and dates keep Windows' regional format.
    /// </remarks>
    public static class UiLanguage
    {
        /// <summary>The choices offered, "" being Windows' own language.</summary>
        public static readonly IReadOnlyList<string> Codes = new[] { "", "en", "fr" };

        /// <summary>Windows' own, read before anything overrides it: what "" goes back to.</summary>
        private static readonly CultureInfo WindowsCulture;

        /// <remarks>
        /// An explicit static constructor, so that the type is initialised at the first call of <see cref="Apply"/>,
        /// before it sets anything. A field initializer alone is run at the first read of a static field: "fr" at start,
        /// then "" in the settings, read it only then, and Windows' language came back as the English just chosen.
        /// </remarks>
        static UiLanguage() => WindowsCulture = CultureInfo.CurrentUICulture;

        /// <summary>Sets the language of every thread from here on; "" or an unknown code is Windows' one.</summary>
        /// <remarks>
        /// The default of every thread only, never <see cref="CultureInfo.CurrentUICulture"/> itself: that one is an
        /// async-local value, so code already under way when the language changes — a loop awaiting its next tick, a
        /// copy to the guest — would have kept the old language for good.
        /// </remarks>
        public static void Apply(string? code)
        {
            CultureInfo.DefaultThreadCurrentUICulture = code switch
            {
                "en" => CultureInfo.GetCultureInfo("en-US"),
                "fr" => CultureInfo.GetCultureInfo("fr-FR"),
                _ => WindowsCulture
            };
        }

        /// <summary>Whether the interface is in French (what is created in a guest follows it: ~/Partages or ~/Shares).</summary>
        public static bool IsFrench => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
    }
}
