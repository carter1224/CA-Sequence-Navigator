using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SequenceNavigator
{
    public sealed class AppSettings
    {
        // The initialisers below are the values a fresh install starts with. Once the
        // user changes them — from Advanced Settings or the PLC Connection dialog — the
        // new values are written to settings.json and used from then on.
        public string PlcIp { get; set; } = "192.168.1.11";
        public int EthSlot { get; set; } = 1;
        public int CpuSlot { get; set; } = 0;
        public int RetryCount { get; set; } = 5;
        public double RetryDelaySeconds { get; set; } = 2;
        public double TimeoutSeconds { get; set; } = 5;
        public bool IncludeProgramTags { get; set; } = false;
        public bool ShowDescriptions { get; set; } = true;
        public bool HighlightActive { get; set; } = true;
        public bool EnableDebugLog { get; set; } = false;
        public string LogPath { get; set; } = "SequenceNavigator.log";
        public string LastZipDir { get; set; } = string.Empty;

        /// <summary>
        /// Set when the last Load or Save failed, so callers can tell the user instead of
        /// silently running on defaults or dropping their preferences.
        /// </summary>
        public static string? LastError { get; private set; }

        public static AppSettings Load()
        {
            LastError = null;
            var path = GetSettingsPathOrNull();
            if (path == null)
            {
                return new AppSettings();
            }

            try
            {
                // Absent on first run, which is normal rather than an error.
                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(path);
                var node = JsonNode.Parse(json);
                var settings = node.Deserialize<AppSettings>() ?? new AppSettings();
                MigrateLegacyKeys(node, settings);
                return settings;
            }
            catch (Exception ex)
            {
                LastError = $"Could not read {path}: {ex.Message}";
                return new AppSettings();
            }
        }

        /// <summary>
        /// Settings written before 1.3 stored the connection under "Default*" names.
        /// Carry those over so an existing install keeps the address it was using
        /// instead of silently snapping back to the built-in default.
        /// </summary>
        private static void MigrateLegacyKeys(JsonNode? node, AppSettings settings)
        {
            if (node is not JsonObject obj)
            {
                return;
            }

            if (obj["PlcIp"] is null &&
                obj["DefaultIp"] is JsonValue legacyIp &&
                legacyIp.TryGetValue<string>(out var ip) &&
                !string.IsNullOrWhiteSpace(ip))
            {
                settings.PlcIp = ip;
            }

            if (obj["EthSlot"] is null &&
                obj["DefaultEthSlot"] is JsonValue legacyEth &&
                legacyEth.TryGetValue<int>(out var eth))
            {
                settings.EthSlot = eth;
            }

            if (obj["CpuSlot"] is null &&
                obj["DefaultCpuSlot"] is JsonValue legacyCpu &&
                legacyCpu.TryGetValue<int>(out var cpu))
            {
                settings.CpuSlot = cpu;
            }
        }

        public static bool Save(AppSettings settings)
        {
            LastError = null;
            var path = GetSettingsPathOrNull();
            if (path == null)
            {
                return false;
            }

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Could not write {path}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Resolves (and creates) the settings directory. Returns null and records the
        /// reason if that is not possible, rather than throwing into every caller.
        /// </summary>
        private static string? GetSettingsPathOrNull()
        {
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var settingsDir = Path.Combine(baseDir, "Sequence Navigator");
                Directory.CreateDirectory(settingsDir);
                return Path.Combine(settingsDir, "settings.json");
            }
            catch (Exception ex)
            {
                LastError = $"Could not create the settings folder: {ex.Message}";
                return null;
            }
        }
    }
}

