using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace VU1WPF.BuiltInServer
{
    /// <summary>Per-dial data kept by the built-in server (what the VU1 server keeps in vudials.db).</summary>
    public sealed class StoredDial
    {
        public string Name { get; set; } = "";
        public int EasingDialStep { get; set; } = 2;        // same defaults as the VU1 server database
        public int EasingDialPeriod { get; set; } = 50;
        public int EasingBacklightStep { get; set; } = 5;
        public int EasingBacklightPeriod { get; set; } = 100;
    }

    /// <summary>
    /// Persistence for the built-in server:
    /// <code>
    /// %USERPROFILE%\KaranovicResearch\VU1-DemoApp\builtin-server\
    ///     dials.json         - dial names and easing settings, by UID
    ///     import-state.json  - timestamps of data imported from an external VU1 server
    ///     images\img_UID.png - last image sent to each dial
    /// </code>
    /// </summary>
    internal sealed class BuiltInStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly object _lock = new();
        private readonly string _dialsFile;
        private readonly string _importStateFile;
        private Dictionary<string, StoredDial> _dials;
        private Dictionary<string, DateTime> _importState;

        public string Root { get; }
        public string ImagesDir { get; }

        public BuiltInStore() : this(Path.Combine(AppPaths.DataDir, "builtin-server")) { }

        public BuiltInStore(string root)
        {
            Root = root;
            ImagesDir = Path.Combine(root, "images");
            Directory.CreateDirectory(ImagesDir);
            _dialsFile = Path.Combine(root, "dials.json");
            _importStateFile = Path.Combine(root, "import-state.json");
            _dials = Load<Dictionary<string, StoredDial>>(_dialsFile) ?? new();
            _importState = Load<Dictionary<string, DateTime>>(_importStateFile) ?? new();
        }

        // ------------------------------------------------------------------ dials

        public StoredDial GetOrCreate(string uid)
        {
            lock (_lock)
            {
                if (!_dials.TryGetValue(uid, out var dial))
                {
                    dial = new StoredDial { Name = "Not set" };
                    _dials[uid] = dial;
                }
                return Clone(dial);
            }
        }

        public void Update(string uid, Action<StoredDial> change)
        {
            lock (_lock)
            {
                if (!_dials.TryGetValue(uid, out var dial))
                {
                    dial = new StoredDial { Name = "Not set" };
                    _dials[uid] = dial;
                }
                change(dial);
                Save(_dialsFile, _dials);
            }
        }

        // ------------------------------------------------------------------ images

        public string ImagePath(string uid) => Path.Combine(ImagesDir, $"img_{SafeUid(uid)}.png");

        public byte[]? LoadImage(string uid)
        {
            string path = ImagePath(uid);
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: could not read {Path}", path);
                return null;
            }
        }

        public void SaveImage(string uid, byte[] imageBytes)
        {
            string path = ImagePath(uid);
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, imageBytes);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: could not save {Path}", path);
            }
        }

        // ------------------------------------------------------------------ import bookkeeping

        /// <summary>True if <paramref name="sourceModifiedUtc"/> is newer than what was imported last time for this key.</summary>
        public bool IsNewerThanImported(string key, DateTime sourceModifiedUtc)
        {
            lock (_lock)
            {
                return !_importState.TryGetValue(key, out var last) || sourceModifiedUtc > last;
            }
        }

        public void MarkImported(string key, DateTime sourceModifiedUtc)
        {
            lock (_lock)
            {
                _importState[key] = sourceModifiedUtc;
                Save(_importStateFile, _importState);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string SafeUid(string uid)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) uid = uid.Replace(c, '_');
            return uid;
        }

        private static StoredDial Clone(StoredDial d) => new()
        {
            Name = d.Name,
            EasingDialStep = d.EasingDialStep,
            EasingDialPeriod = d.EasingDialPeriod,
            EasingBacklightStep = d.EasingBacklightStep,
            EasingBacklightPeriod = d.EasingBacklightPeriod,
        };

        private static T? Load<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: could not read {Path}; starting fresh", path);
                return null;
            }
        }

        private static void Save<T>(string path, T data)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: could not save {Path}", path);
            }
        }
    }
}
