using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SequenceNavigator
{
    public sealed class AppSettings
    {
        private const int MaxRecentFiles = 10;
        private const int MaxRecentPlcs = 8;
        private const int MaxRememberedPositions = 30;

        // The initialisers below are the values a fresh install starts with. Once the
        // user changes them — from Settings or the PLC Connection dialog — the new values
        // are written to settings.json and used from then on.
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

        /// <summary>Most recent first. Shown on the start screen.</summary>
        public List<string> RecentFiles { get; set; } = new();

        /// <summary>Most recent first. Offered in the PLC Connection dialog.</summary>
        public List<RecentPlc> RecentPlcs { get; set; } = new();

        /// <summary>Null until the window has been closed once.</summary>
        public WindowPlacement? Window { get; set; }

        /// <summary>Where each backup was last left, keyed by full path.</summary>
        public Dictionary<string, ViewPosition> LastPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Set when the last Load or Save failed, so callers can tell the user instead of
        /// silently running on defaults or dropping their preferences.
        /// </summary>
        public static string? LastError { get; private set; }

        /// <summary>
        /// %LocalAppData%\Sequence Navigator: settings, logs and anything else per-user.
        /// Null if it could not be created.
        /// </summary>
        public static string? DataDirectory => GetDataDirectoryOrNull();

        /// <summary>
        /// A relative log path lives in the per-user data folder, not beside the exe, which
        /// may be read-only (Program Files, a network share).
        /// </summary>
        public string ResolveLogPath()
        {
            var logPath = string.IsNullOrWhiteSpace(LogPath) ? "SequenceNavigator.log" : LogPath;
            return Path.IsPathRooted(logPath)
                ? logPath
                : Path.Combine(DataDirectory ?? AppDomain.CurrentDomain.BaseDirectory, logPath);
        }

        public void AddRecentFile(string path)
        {
            RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, path);
            if (RecentFiles.Count > MaxRecentFiles)
            {
                RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
            }
        }

        public void RemoveRecentFile(string path) =>
            RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Records a controller that answered. A later call with only an address keeps the
        /// name and model already known for it.
        /// </summary>
        public void AddRecentPlc(string ip, int cpuSlot, string? name = null, string? model = null)
        {
            var existing = RecentPlcs.FirstOrDefault(p =>
                string.Equals(p.Ip, ip, StringComparison.OrdinalIgnoreCase) && p.Slot == cpuSlot);
            RecentPlcs.RemoveAll(p =>
                string.Equals(p.Ip, ip, StringComparison.OrdinalIgnoreCase) && p.Slot == cpuSlot);
            RecentPlcs.Insert(0, new RecentPlc
            {
                Ip = ip,
                Slot = cpuSlot,
                Name = name ?? existing?.Name,
                Model = model ?? existing?.Model,
            });
            if (RecentPlcs.Count > MaxRecentPlcs)
            {
                RecentPlcs.RemoveRange(MaxRecentPlcs, RecentPlcs.Count - MaxRecentPlcs);
            }
        }

        public void RememberPosition(string path, ViewPosition position)
        {
            LastPositions[path] = position;
            if (LastPositions.Count <= MaxRememberedPositions)
            {
                return;
            }

            // Bounded, not ordered: files that no longer exist go first, then any others
            // until the cap is met. Losing a remembered step is harmless.
            var excess = LastPositions.Keys
                .Where(k => !string.Equals(k, path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => File.Exists(k))
                .Take(LastPositions.Count - MaxRememberedPositions)
                .ToList();
            foreach (var key in excess)
            {
                LastPositions.Remove(key);
            }
        }

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
                // The deserialiser builds a case-sensitive dictionary; paths are not.
                settings.LastPositions = new Dictionary<string, ViewPosition>(
                    settings.LastPositions ?? new(), StringComparer.OrdinalIgnoreCase);
                settings.RecentFiles ??= new();
                settings.RecentPlcs ??= new();
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

        private static string? GetSettingsPathOrNull()
        {
            var dir = GetDataDirectoryOrNull();
            return dir == null ? null : Path.Combine(dir, "settings.json");
        }

        /// <summary>
        /// Resolves (and creates) the per-user data directory. Returns null and records the
        /// reason if that is not possible, rather than throwing into every caller.
        /// </summary>
        private static string? GetDataDirectoryOrNull()
        {
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dataDir = Path.Combine(baseDir, "Sequence Navigator");
                Directory.CreateDirectory(dataDir);
                return dataDir;
            }
            catch (Exception ex)
            {
                LastError = $"Could not create the settings folder: {ex.Message}";
                return null;
            }
        }
    }

    public sealed class RecentPlc
    {
        public string Ip { get; set; } = string.Empty;
        public int Slot { get; set; }
        public string? Name { get; set; }
        public string? Model { get; set; }

        /// <summary>What the address drop-down shows beside the IP.</summary>
        [JsonIgnore]
        public string Detail =>
            string.Join(" · ", new[] { Name, Model, $"slot {Slot}" }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    public sealed class ViewPosition
    {
        public string? Sequence { get; set; }
        public int Step { get; set; } = 1;
        public int Tab { get; set; }
    }
}
