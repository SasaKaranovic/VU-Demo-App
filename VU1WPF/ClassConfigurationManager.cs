using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VU1WPF;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;


namespace KR_VU1_ConfigurationManager
{
    /// <summary>
    /// Loads/saves the YAML configuration.
    /// - Thread-safe (the dial update loop and the UI both read it).
    /// - Saves atomically (write temp file, then replace) so a crash/power loss can't corrupt the config.
    /// - A corrupt config is backed up and replaced with defaults instead of stopping the app.
    /// </summary>
    public class ClassConfigurationManager
    {
        private const float default_update_period = 0.5f;
        private const float min_update_period = 0.2f;
        // Default master key of a fresh VU1 server installation
        private const string default_master_key = "cTpAWYuRpA2zx75Yh961Cg";

        private readonly object _lock = new();
        private readonly object _saveLock = new();
        private readonly string _configPath;
        private ConfigContentsRoot localConfig = new();

        /// <summary>Set when the config file could not be parsed and was reset to defaults.</summary>
        public string? LoadWarning { get; private set; }

        public ClassConfigurationManager() : this(AppPaths.ConfigFile) { }

        public ClassConfigurationManager(string configPath)
        {
            _configPath = configPath;

            LoadConfigFile();
            Normalize();

            Log.Information("Dial update period: {Period} seconds", localConfig.dialUpdatePeriod);

            SaveConfigFile();   // Persist defaults/normalized values
        }

        // ------------------------------------------------------------------ getters

        public float GetDialUpdatePeriod()
        {
            lock (_lock) return localConfig.dialUpdatePeriod;
        }

        public String GetMasterKey()
        {
            lock (_lock) return localConfig.masterKey;
        }

        public string GetServerUrl()
        {
            lock (_lock) return localConfig.serverUrl;
        }

        /// <summary>Updates connection settings from the Settings window and saves the config.</summary>
        public void UpdateServerSettings(string serverUrl, string apiKey, float updatePeriod)
        {
            lock (_lock)
            {
                localConfig.serverUrl = serverUrl;
                localConfig.masterKey = string.IsNullOrWhiteSpace(apiKey) ? default_master_key : apiKey.Trim();
                localConfig.dialUpdatePeriod = Math.Max(min_update_period, updatePeriod);
            }
            SaveConfigFile();
        }

        public string GetDialMetric(String uid)
        {
            lock (_lock) return FindDial(uid)?.sensor_identifier ?? "";
        }

        public float GetDialMin(String uid)
        {
            lock (_lock) return FindDial(uid)?.scaling_min ?? 0;
        }

        public float GetDialMax(String uid)
        {
            lock (_lock) return FindDial(uid)?.scaling_max ?? 100;
        }

        public string GetDialDesign(String uid)
        {
            lock (_lock) return FindDial(uid)?.image_design ?? "";
        }

        public string GetDialFinish(String uid)
        {
            lock (_lock) return NormalizeFinish(FindDial(uid)?.finish);
        }

        /// <summary>Rules on/off. Older configs without the flag: on when rules exist.</summary>
        public bool GetDialRulesEnabled(String uid)
        {
            lock (_lock)
            {
                var dial = FindDial(uid);
                if (dial == null) return true;
                return dial.rules_enabled ?? (dial.thresholds != null && dial.thresholds.Count > 0);
            }
        }

        public (int r, int g, int b)? GetDialFixedColor(String uid)
        {
            lock (_lock)
            {
                var c = FindDial(uid)?.fixed_color;
                if (c == null || c.Count < 3) return null;
                return (Math.Clamp(c[0], 0, 100), Math.Clamp(c[1], 0, 100), Math.Clamp(c[2], 0, 100));
            }
        }

        public const int MinIdentifyMs = 200;
        public const int MaxIdentifyMs = 10000;

        public (int r, int g, int b) GetIdentifyColor()
        {
            lock (_lock)
            {
                var c = localConfig.identifyColor;
                if (c == null || c.Count < 3) return (100, 100, 100);
                return (Math.Clamp(c[0], 0, 100), Math.Clamp(c[1], 0, 100), Math.Clamp(c[2], 0, 100));
            }
        }

        public int GetIdentifyDurationMs()
        {
            lock (_lock) return Math.Clamp(localConfig.identifyDurationMs, MinIdentifyMs, MaxIdentifyMs);
        }

        public void SetIdentifySettings((int r, int g, int b) color, int durationMs, bool saveAfter = true)
        {
            lock (_lock)
            {
                localConfig.identifyColor = new List<int> { color.r, color.g, color.b };
                localConfig.identifyDurationMs = Math.Clamp(durationMs, MinIdentifyMs, MaxIdentifyMs);
            }
            if (saveAfter) SaveConfigFile();
        }

        public bool GetMetricsExpanded()
        {
            lock (_lock) return localConfig.metricsExpanded;
        }

        public void SetMetricsExpanded(bool expanded)
        {
            lock (_lock) localConfig.metricsExpanded = expanded;
            SaveConfigFile();
        }

        public List<string> GetDialOrder()
        {
            lock (_lock) return new List<string>(localConfig.dialOrder);
        }

        public void SetDialOrder(IEnumerable<string> uids)
        {
            lock (_lock) localConfig.dialOrder = uids.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
            SaveConfigFile();
        }

        public static string NormalizeFinish(string? finish) =>
            string.Equals(finish, "silver", StringComparison.OrdinalIgnoreCase) ? "silver" : "black";

        public List<ClassDialThreshold> GetDialThresholds(String uid)
        {
            lock (_lock)
            {
                var dial = FindDial(uid);
                if (dial?.thresholds == null)
                {
                    return new List<ClassDialThreshold>();
                }

                return dial.thresholds
                    .Select(item => new ClassDialThreshold
                    {
                        Threshold = item.value,
                        BacklightRed = item.red,
                        BacklightGreen = item.green,
                        BacklightBlue = item.blue
                    })
                    .OrderBy(t => t.Threshold)
                    .ToList();
            }
        }

        // ------------------------------------------------------------------ setters

        public bool UpdateDialConfig(ClassDialGUI dialGui, bool saveAfter)
        {
            if (dialGui == null || string.IsNullOrEmpty(dialGui.UID)) return false;

            lock (_lock)
            {
                // Same code path for new and existing dials.
                // (Previously a new dial stored `Metric` - which was never set - and skipped the
                //  scaling values, so the first "Save" of a dial lost its sensor.)
                var dial = GetOrCreateDial(dialGui.UID);
                dial.scaling_min = dialGui.ScaleMin;
                dial.scaling_max = dialGui.ScaleMax;
                dial.thresholds = DialGUI_to_ConfigThresholds(dialGui.Thresholds);
                // Keep the configured identifier if the sensor is (temporarily) missing on this computer
                dial.sensor_identifier = dialGui.Sensor != null ? dialGui.Sensor.Identifier.ToString() : (dialGui.SensorIdentifier ?? "");
                dial.finish = NormalizeFinish(dialGui.Finish);
                dial.rules_enabled = dialGui.RulesEnabled;
                dial.fixed_color = dialGui.FixedColor.HasValue
                    ? new List<int> { dialGui.FixedColor.Value.r, dialGui.FixedColor.Value.g, dialGui.FixedColor.Value.b }
                    : null;
            }

            if (saveAfter)
            {
                SaveConfigFile();
            }

            return true;
        }

        public void SetDialDesign(string uid, string designName, bool saveAfter = true)
        {
            if (string.IsNullOrEmpty(uid)) return;

            lock (_lock)
            {
                GetOrCreateDial(uid).image_design = designName ?? "";
            }

            if (saveAfter)
            {
                SaveConfigFile();
            }
        }

        // ------------------------------------------------------------------ persistence

        public void SaveConfigFile()
        {
            lock (_saveLock)
            {
                SaveConfigFileCore();
            }
        }

        private void SaveConfigFileCore()
        {
            string tmpPath = _configPath + ".tmp";

            try
            {
                string yaml;
                lock (_lock)
                {
                    var serializer = new SerializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build();
                    yaml = serializer.Serialize(localConfig);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

                // Write + flush to a temp file, then atomically swap it in.
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(yaml);
                    sw.Flush();
                    fs.Flush(true);
                }

                File.Move(tmpPath, _configPath, overwrite: true);
                Log.Debug("Config file saved.");
            }
            catch (Exception ex)
            {
                // Don't crash the app because the config could not be written (e.g. file locked by AV/backup).
                Log.Error(ex, "Failed to save config file {Path}", _configPath);
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
            }
        }

        private void LoadConfigFile()
        {
            if (!File.Exists(_configPath))
            {
                Log.Information("Config file does not exist. Using defaults: {Path}", _configPath);
                localConfig = new ConfigContentsRoot
                {
                    dialUpdatePeriod = default_update_period,
                    masterKey = default_master_key
                };
                return;
            }

            try
            {
                string fileContents = File.ReadAllText(_configPath, Encoding.UTF8);
                Log.Verbose("Config contents:\n{Contents}", fileContents);

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var parsed = deserializer.Deserialize<ConfigContentsRoot>(fileContents);
                if (parsed == null)
                {
                    throw new InvalidDataException("Config file is empty.");
                }

                localConfig = parsed;
                foreach (var dial in localConfig.dial_metrics ?? new List<ConfigContentsDial>())
                {
                    Log.Debug("{Uid} uses {Sensor}.", dial?.dial_uid, dial?.sensor_identifier);
                }
            }
            catch (Exception e)
            {
                // Keep the broken file for troubleshooting and continue with defaults.
                string backup = _configPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Log.Error(e, "Failed to load config. Backing it up to {Backup} and using defaults.", backup);
                try { File.Copy(_configPath, backup, overwrite: true); } catch (Exception copyEx) { Log.Error(copyEx, "Config backup failed"); }

                LoadWarning = $"The configuration file could not be read and was reset to defaults.\nA copy of the old file was saved as:\n{backup}";
                localConfig = new ConfigContentsRoot
                {
                    dialUpdatePeriod = default_update_period,
                    masterKey = default_master_key
                };
            }
        }

        /// <summary>Fix up missing/invalid values (nulls from YAML, too-short update period, ...).</summary>
        private void Normalize()
        {
            lock (_lock)
            {
                if (localConfig.dialUpdatePeriod <= 0 || float.IsNaN(localConfig.dialUpdatePeriod))
                {
                    Log.Warning("Config has no valid dial update period. Using {Default}s.", default_update_period);
                    localConfig.dialUpdatePeriod = default_update_period;
                }
                else if (localConfig.dialUpdatePeriod < min_update_period)
                {
                    Log.Warning("Config requests dial update period of {Period}s. Limiting to {Min}s.", localConfig.dialUpdatePeriod, min_update_period);
                    localConfig.dialUpdatePeriod = min_update_period;
                }

                if (!KR_VU1_Server.VU1_Server.TryNormalizeServerUrl(localConfig.serverUrl, out string url))
                {
                    if (!string.IsNullOrWhiteSpace(localConfig.serverUrl))
                    {
                        Log.Warning("Invalid server URL `{Url}` in config. Using default.", localConfig.serverUrl);
                    }
                    url = KR_VU1_Server.VU1_Server.DefaultServerUrl;
                }
                localConfig.serverUrl = url;

                if (string.IsNullOrWhiteSpace(localConfig.masterKey))
                {
                    Log.Warning("No master key in config. Using default key.");
                    localConfig.masterKey = default_master_key;
                }

                localConfig.dialOrder ??= new List<string>();
                if (localConfig.identifyColor == null || localConfig.identifyColor.Count < 3)
                {
                    localConfig.identifyColor = new List<int> { 100, 100, 100 };
                }
                if (localConfig.identifyDurationMs <= 0) localConfig.identifyDurationMs = 1500;
                localConfig.dial_metrics ??= new List<ConfigContentsDial>();
                localConfig.dial_metrics.RemoveAll(d => d == null || string.IsNullOrEmpty(d.dial_uid));

                foreach (var dial in localConfig.dial_metrics)
                {
                    dial.sensor_identifier ??= "";
                    dial.thresholds ??= new List<ConfigThreshold>();
                    dial.thresholds.RemoveAll(t => t == null);
                    dial.image_design ??= "";
                    dial.finish = NormalizeFinish(dial.finish);
                }

                // Drop duplicate entries for the same dial (keep the first one)
                localConfig.dial_metrics = localConfig.dial_metrics
                    .GroupBy(d => d.dial_uid)
                    .Select(g => g.First())
                    .ToList();
            }
        }

        // ------------------------------------------------------------------ helpers (call under _lock)

        private ConfigContentsDial? FindDial(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            return localConfig.dial_metrics.Find(item => item.dial_uid == uid);
        }

        private ConfigContentsDial GetOrCreateDial(string uid)
        {
            var dial = FindDial(uid);
            if (dial == null)
            {
                dial = new ConfigContentsDial { dial_uid = uid };
                localConfig.dial_metrics.Add(dial);
            }
            return dial;
        }

        private static List<ConfigThreshold> DialGUI_to_ConfigThresholds(List<ClassDialThreshold>? dialThresholds)
        {
            if (dialThresholds == null)
            {
                return new List<ConfigThreshold>();
            }

            return dialThresholds
                .Where(t => t != null)
                .Select(item => new ConfigThreshold
                {
                    value = item.Threshold,
                    red = item.BacklightRed,
                    green = item.BacklightGreen,
                    blue = item.BacklightBlue
                })
                .ToList();
        }
    }
}
