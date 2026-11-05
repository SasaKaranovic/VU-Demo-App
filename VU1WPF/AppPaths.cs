using System;
using System.IO;

namespace VU1WPF
{
    /// <summary>
    /// Central place for every file/folder location used by the app.
    /// All paths are absolute so the app works regardless of the current working
    /// directory (e.g. when started from the Windows "Run" registry key).
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Folder containing the executable and bundled content.</summary>
        public static string AppDir => AppContext.BaseDirectory;

        /// <summary>%USERPROFILE%\KaranovicResearch\VU1-DemoApp</summary>
        public static string DataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "KaranovicResearch", "VU1-DemoApp");

        public static string ConfigFile => Path.Combine(DataDir, "vu1demo_config.yaml");
        public static string LogFile => Path.Combine(DataDir, "log.txt");

        /// <summary>Saved image designs (one sub-folder per design).</summary>
        public static string DesignsDir => Path.Combine(DataDir, "designs");

        /// <summary>User supplied templates. Any image dropped here shows up in the image editor.</summary>
        public static string UserTemplatesDir => Path.Combine(DataDir, "templates");

        /// <summary>Templates shipped with the app (copied from /image_pack at build time).</summary>
        public static string BundledTemplatesDir => Path.Combine(AppDir, "Templates");

        public static string IconIco => Path.Combine(AppDir, "VU1_Icon.ico");

        public static void EnsureDataDirs()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(DesignsDir);
            Directory.CreateDirectory(UserTemplatesDir);
        }
    }
}
