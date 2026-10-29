using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinQEMU.Controls
{
    /// <summary>
    /// A theme brush for an element's own theme, for what is drawn from code. Application.Current.Resources answers in
    /// the application's theme, Windows' one, whatever theme the window was given in the settings: with WinQEMU in
    /// light on a dark Windows, the snapshot tree wrote « État d'origine » white on white.
    /// </summary>
    internal static class ThemeBrushes
    {
        public static Brush? Get(FrameworkElement element, string key)
        {
            var theme = element.ActualTheme == ElementTheme.Light ? "Light" : "Default";
            return Find(Application.Current.Resources, theme, key)
                   ?? (Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null);
        }

        private static Brush? Find(ResourceDictionary dictionary, string theme, string key)
        {
            // The dark theme's dictionary is "Default" in WinUI's own resources, "Dark" in some others.
            foreach (var name in theme == "Light" ? new[] { "Light" } : new[] { "Default", "Dark" })
            {
                if (dictionary.ThemeDictionaries.TryGetValue(name, out var themed) && themed is ResourceDictionary themedDictionary
                    && themedDictionary.TryGetValue(key, out var value) && value is Brush brush)
                    return brush;
            }
            foreach (var merged in dictionary.MergedDictionaries)
            {
                if (Find(merged, theme, key) is { } found)
                    return found;
            }
            return null;
        }
    }
}
