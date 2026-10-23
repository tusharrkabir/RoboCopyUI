using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace RoboCopyUI
{
    public static class ThemeManager
    {
        public static string CurrentTheme { get; private set; } = "Dark";

        private static Uri GetThemeUri(string themeName)
        {
            string file = themeName.ToLowerInvariant() switch
            {
                "light" => "LightTheme.xaml",
                "midnight" => "MidnightTheme.xaml",
                _ => "DarkTheme.xaml"
            };

            return new Uri($"/Themes/{file}", UriKind.Relative);
        }

        public static void ApplyTheme(string themeName)
        {
            try
            {
                CurrentTheme = themeName;

                var newThemeUri = GetThemeUri(themeName);
                var newThemeDict = new ResourceDictionary { Source = newThemeUri };

                if (System.Windows.Application.Current != null)
                {
                    UpdateMergedDictionary(System.Windows.Application.Current.Resources.MergedDictionaries, newThemeDict);

                    foreach (Window window in System.Windows.Application.Current.Windows.OfType<Window>())
                    {
                        ApplyThemeToWindow(window, themeName);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply theme '{themeName}': {ex.Message}");
            }
        }

        public static void ApplyThemeToWindow(Window window, string? themeName = null)
        {
            themeName ??= CurrentTheme;

            var newThemeUri = GetThemeUri(themeName);
            var newThemeDict = new ResourceDictionary { Source = newThemeUri };

            UpdateMergedDictionary(window.Resources.MergedDictionaries, newThemeDict);
            bool isDarkVariant = !themeName.Equals("Light", StringComparison.OrdinalIgnoreCase);
            WindowBlurHelper.EnableBlur(window, isDarkVariant);
        }

        private static void UpdateMergedDictionary(Collection<ResourceDictionary> mergedDicts, ResourceDictionary newDict)
        {
            var oldDict = mergedDicts.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme.xaml"));
            if (oldDict != null)
            {
                mergedDicts.Remove(oldDict);
            }
            mergedDicts.Add(newDict);
        }
    }
}
