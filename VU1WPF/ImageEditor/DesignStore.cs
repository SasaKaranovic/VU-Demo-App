using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VU1WPF.ImageEditor
{
    /// <summary>
    /// Saves/loads image designs. Layout on disk:
    /// <code>
    /// %USERPROFILE%\KaranovicResearch\VU1-DemoApp\designs\&lt;name&gt;\
    ///     design.json   - text, strokes, processing options
    ///     base.&lt;ext&gt;    - copy of the original base image (optional)
    ///     output.png    - last rendered 200x144 1-bit image (preview / re-send)
    /// </code>
    /// </summary>
    public class DesignStore
    {
        public const string DesignFileName = "design.json";
        public const string OutputFileName = "output.png";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _root;

        public DesignStore() : this(AppPaths.DesignsDir) { }

        public DesignStore(string root)
        {
            _root = root;
            Directory.CreateDirectory(_root);
        }

        public static string ToSafeName(string name)
        {
            name = (name ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            name = name.Trim('.', ' ');
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            return name;
        }

        public string GetFolder(string name) => Path.Combine(_root, ToSafeName(name));

        public bool Exists(string name)
        {
            string safe = ToSafeName(name);
            return safe.Length > 0 && File.Exists(Path.Combine(_root, safe, DesignFileName));
        }

        public List<DesignInfo> List()
        {
            var result = new List<DesignInfo>();
            if (!Directory.Exists(_root)) return result;

            foreach (string dir in Directory.EnumerateDirectories(_root))
            {
                string jsonPath = Path.Combine(dir, DesignFileName);
                if (!File.Exists(jsonPath)) continue;

                string name = Path.GetFileName(dir);
                DateTime modified = File.GetLastWriteTimeUtc(jsonPath);
                try
                {
                    var design = JsonSerializer.Deserialize<DialDesign>(File.ReadAllText(jsonPath), JsonOptions);
                    if (design != null)
                    {
                        if (!string.IsNullOrWhiteSpace(design.Name)) name = design.Name;
                        modified = design.ModifiedUtc;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not read design {Path}", jsonPath);
                }

                string preview = Path.Combine(dir, OutputFileName);
                result.Add(new DesignInfo
                {
                    Name = name,
                    Folder = dir,
                    PreviewPath = File.Exists(preview) ? preview : null,
                    ModifiedUtc = modified,
                });
            }

            return result.OrderByDescending(d => d.ModifiedUtc).ToList();
        }

        /// <summary>Loads a design and its base image bytes (null when the design has no base image).</summary>
        public (DialDesign design, byte[]? baseImage) Load(string name)
        {
            string folder = GetFolder(name);
            string jsonPath = Path.Combine(folder, DesignFileName);

            var design = JsonSerializer.Deserialize<DialDesign>(File.ReadAllText(jsonPath), JsonOptions)
                         ?? throw new InvalidDataException("Design file is empty: " + jsonPath);
            design.Texts ??= new List<DesignTextItem>();

            byte[]? baseImage = null;
            if (!string.IsNullOrEmpty(design.BaseImageFile))
            {
                string basePath = Path.Combine(folder, Path.GetFileName(design.BaseImageFile));
                if (File.Exists(basePath))
                {
                    baseImage = File.ReadAllBytes(basePath);
                }
                else
                {
                    Log.Warning("Base image {Path} of design {Name} is missing", basePath, name);
                    design.BaseImageFile = null;
                }
            }

            return (design, baseImage);
        }

        /// <summary>Loads the last rendered output PNG of a design (for quick re-send).</summary>
        public byte[]? LoadOutputPng(string name)
        {
            string path = Path.Combine(GetFolder(name), OutputFileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        /// <summary>Saves (creates or overwrites) a design.</summary>
        /// <param name="baseImage">Original base image bytes, or null for a blank background.</param>
        /// <param name="baseImageExtension">Extension of the base image, e.g. ".png".</param>
        /// <param name="outputPng">Rendered output image.</param>
        public void Save(DialDesign design, byte[]? baseImage, string? baseImageExtension, byte[]? outputPng)
        {
            string safe = ToSafeName(design.Name);
            if (safe.Length == 0) throw new ArgumentException("Design name is empty.");

            string folder = Path.Combine(_root, safe);
            Directory.CreateDirectory(folder);

            // Base image
            foreach (string old in Directory.EnumerateFiles(folder, "base.*"))
            {
                TryDelete(old);
            }
            if (baseImage != null && baseImage.Length > 0)
            {
                string ext = string.IsNullOrWhiteSpace(baseImageExtension) ? ".png" : baseImageExtension.ToLowerInvariant();
                if (!ext.StartsWith('.')) ext = "." + ext;
                design.BaseImageFile = "base" + ext;
                WriteAtomic(Path.Combine(folder, design.BaseImageFile), baseImage);
            }
            else
            {
                design.BaseImageFile = null;
            }

            if (outputPng != null)
            {
                WriteAtomic(Path.Combine(folder, OutputFileName), outputPng);
            }

            design.ModifiedUtc = DateTime.UtcNow;
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(design, JsonOptions);
            WriteAtomic(Path.Combine(folder, DesignFileName), json);

            Log.Information("Saved design {Name} to {Folder}", design.Name, folder);
        }

        public void Delete(string name)
        {
            string folder = GetFolder(name);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                Log.Information("Deleted design {Name}", name);
            }
        }

        private static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception ex) { Log.Warning(ex, "Could not delete {Path}", path); }
        }
    }
}
