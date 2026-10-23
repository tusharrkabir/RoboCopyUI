using System;
using System.IO;
using System.Text.Json;

namespace RoboCopyUI
{
    public class UserSettings
    {
        public int SettingsVersion { get; set; } = 2;
        public string Theme { get; set; } = "Dark"; // "Dark" or "Light"
        public int ThreadCount { get; set; } = 8;
        public bool AutoSelectThreads { get; set; } = true;
        public bool UnbufferedIO { get; set; } = false;
        public bool PreserveTimestamps { get; set; } = true;
        public bool PreservePermissions { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public int WaitSeconds { get; set; } = 0;

        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboCopyUI");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    if (settings != null)
                    {
                        // Migration: Upgrade existing settings files to enable AutoSelectThreads by default
                        if (settings.SettingsVersion < 2)
                        {
                            settings.AutoSelectThreads = true;
                            settings.SettingsVersion = 2;
                            settings.Save();
                        }
                        return settings;
                    }
                }
            }
            catch
            {
                // Fallback to defaults on read error
            }

            var defaults = new UserSettings { AutoSelectThreads = true, SettingsVersion = 2 };
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save user settings: {ex.Message}");
            }
        }
    }
}
