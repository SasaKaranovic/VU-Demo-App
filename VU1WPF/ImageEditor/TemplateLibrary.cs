using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace VU1WPF.ImageEditor
{
    /// <summary>
    /// Finds template images. Two locations are scanned:
    ///  1. "Templates" folder next to the executable (built from /image_pack)
    ///  2. %USERPROFILE%\KaranovicResearch\VU1-DemoApp\templates (user additions)
    /// Any PNG/JPG/BMP/GIF dropped into either folder appears in the editor automatically.
    /// Templates should ideally be 200x144 pixels.
    /// </summary>
    public static class TemplateLibrary
    {
        public static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        public static string FileDialogFilter =>
            "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";

        /// <summary>All template files as (path, source label), without decoding them.</summary>
        public static List<(string path, string source)> ListPaths()
        {
            var result = new List<(string, string)>();
            result.AddRange(Scan(AppPaths.BundledTemplatesDir).Select(p => (p, "Built-in")));
            result.AddRange(Scan(AppPaths.UserTemplatesDir).Select(p => (p, "My templates")));
            return result;
        }

        public static List<TemplateInfo> LoadAll()
        {
            return ListPaths()
                .Select(t => new TemplateInfo
                {
                    Name = Path.GetFileNameWithoutExtension(t.path),
                    Path = t.path,
                    Source = t.source,
                    Thumbnail = DialImageRenderer.TryLoadThumbnail(t.path, 100),
                })
                .Where(t => t.Thumbnail != null)
                .ToList();
        }

        private static List<string> Scan(string folder)
        {
            if (!Directory.Exists(folder)) return new List<string>();

            try
            {
                return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not scan template folder {Folder}", folder);
                return new List<string>();
            }
        }
    }
}
