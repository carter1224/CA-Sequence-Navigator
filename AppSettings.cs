using System;
using System.IO;
using System.Text.Json;

namespace SequenceNavigator
{
    public sealed class AppSettings
    {
        public string DefaultIp { get; set; } = "192.168.1.11";
        public int DefaultEthSlot { get; set; } = 1;
        public int DefaultCpuSlot { get; set; } = 0;
        public int RetryCount { get; set; } = 5;
        public double RetryDelaySeconds { get; set; } = 2;
        public double TimeoutSeconds { get; set; } = 5;
        public bool IncludeProgramTags { get; set; } = false;
        public bool ShowDescriptions { get; set; } = true;
        public bool HighlightActive { get; set; } = true;
        public bool EnableDebugLog { get; set; } = false;
        public string LogPath { get; set; } = "SequenceNavigator.log";
        public string LastZipDir { get; set; } = string.Empty;

        public static AppSettings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var path = GetSettingsPath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private static string GetSettingsPath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsDir = Path.Combine(baseDir, "Sequence Navigator");
            Directory.CreateDirectory(settingsDir);
            return Path.Combine(settingsDir, "settings.json");
        }
    }
}

