using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Serilog;

namespace VU1WPF.BuiltInServer
{
    /// <summary>
    /// Imports dial names, easing settings and dial images from an installed (external) VU1 server,
    /// so switching to the built-in server keeps them. Runs every time the built-in server starts and
    /// only takes data that changed since the last import (so names changed in the app are not
    /// overwritten by stale server data).
    ///
    /// The VU1 server keeps its data next to its code: vudials.db (SQLite) and upload\img_UID.
    /// For the installed (PyInstaller) build that is the install folder or its "_internal" subfolder.
    /// </summary>
    internal static class ExternalServerImporter
    {
        private const string DatabaseFile = "vudials.db";
        private const string UploadFolder = "upload";

        /// <summary>Folder holding vudials.db and/or upload\, or null if no VU1 server data was found.</summary>
        public static string? FindDataFolder(string? configuredFolder)
        {
            foreach (string baseDir in CandidateFolders(configuredFolder))
            {
                foreach (string dir in new[] { baseDir, Path.Combine(baseDir, "_internal") })
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir, DatabaseFile)) || Directory.Exists(Path.Combine(dir, UploadFolder)))
                        {
                            return dir;
                        }
                    }
                    catch (Exception)
                    {
                        // inaccessible folder - try the next one
                    }
                }
            }
            return null;
        }

        /// <summary>Imports changed data into <paramref name="store"/>. Returns a short summary for the log.</summary>
        public static string Import(BuiltInStore store, string? configuredFolder)
        {
            string? folder = FindDataFolder(configuredFolder);
            if (folder == null)
            {
                Log.Information("Built-in server: no external VU1 server data found to import");
                return "no external data found";
            }

            int names = 0, images = 0;
            try
            {
                names = ImportDatabase(store, Path.Combine(folder, DatabaseFile));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: importing {Db} failed", Path.Combine(folder, DatabaseFile));
            }

            try
            {
                images = ImportImages(store, Path.Combine(folder, UploadFolder));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: importing images from {Folder} failed", folder);
            }

            string summary = $"imported {names} dial setting(s) and {images} image(s) from {folder}";
            Log.Information("Built-in server: {Summary}", summary);
            return summary;
        }

        private static int ImportDatabase(BuiltInStore store, string dbPath)
        {
            if (!File.Exists(dbPath)) return 0;

            DateTime modified = File.GetLastWriteTimeUtc(dbPath);
            const string key = "db";
            if (!store.IsNewerThanImported(key, modified)) return 0;

            int count = 0;
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT dial_uid, dial_name, easing_dial_step, easing_dial_period, " +
                                  "easing_backlight_step, easing_backlight_period FROM dials";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string uid = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(uid)) continue;

                    string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
                    int? dialStep = ReadInt(reader, 2), dialPeriod = ReadInt(reader, 3);
                    int? blStep = ReadInt(reader, 4), blPeriod = ReadInt(reader, 5);

                    store.Update(uid, d =>
                    {
                        if (!string.IsNullOrWhiteSpace(name)) d.Name = name;
                        if (dialStep.HasValue) d.EasingDialStep = dialStep.Value;
                        if (dialPeriod.HasValue) d.EasingDialPeriod = dialPeriod.Value;
                        if (blStep.HasValue) d.EasingBacklightStep = blStep.Value;
                        if (blPeriod.HasValue) d.EasingBacklightPeriod = blPeriod.Value;
                    });
                    count++;
                }
            }

            store.MarkImported(key, modified);
            return count;
        }

        private static int? ReadInt(SqliteDataReader reader, int column)
        {
            if (reader.IsDBNull(column)) return null;
            object value = reader.GetValue(column);
            return value switch
            {
                long l => (int)l,
                double d => (int)d,
                string s when int.TryParse(s, out int i) => i,
                _ => null,
            };
        }

        private static int ImportImages(BuiltInStore store, string uploadDir)
        {
            if (!Directory.Exists(uploadDir)) return 0;

            int count = 0;
            foreach (string file in Directory.EnumerateFiles(uploadDir, "img_*"))
            {
                string uid = Path.GetFileName(file).Substring(4);
                if (uid.Length == 0 || uid.Equals("blank", StringComparison.OrdinalIgnoreCase)) continue;

                DateTime modified = File.GetLastWriteTimeUtc(file);
                string key = "img:" + uid;
                if (!store.IsNewerThanImported(key, modified)) continue;

                store.SaveImage(uid, File.ReadAllBytes(file));
                store.MarkImported(key, modified);
                count++;
            }
            return count;
        }

        /// <summary>Places where an installed VU1 server may live.</summary>
        private static IEnumerable<string> CandidateFolders(string? configuredFolder)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string?> All()
            {
                yield return configuredFolder;

                // A running VU1 server tells us exactly where it is
                foreach (var p in SafeProcesses("VUServer"))
                {
                    yield return p;
                }

                // Installer entries ("Add/Remove programs")
                foreach (var p in UninstallLocations())
                {
                    yield return p;
                }

                foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                           Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                {
                    if (string.IsNullOrEmpty(pf)) continue;
                    yield return Path.Combine(pf, "KaranovicResearch", "VUDials");
                    yield return Path.Combine(pf, "KaranovicResearch", "VUServer");
                    yield return Path.Combine(pf, "KaranovicResearch", "VU Server");
                    yield return Path.Combine(pf, "VUServer");
                }
            }

            foreach (string? dir in All())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string clean = dir.Trim().Trim('"');
                if (seen.Add(clean) && Directory.Exists(clean)) yield return clean;
            }
        }

        private static IEnumerable<string> SafeProcesses(string name)
        {
            var result = new List<string>();
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string? exe = p.MainModule?.FileName;
                        if (exe != null) result.Add(Path.GetDirectoryName(exe)!);
                    }
                    catch (Exception)
                    {
                        // access denied etc.
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                // ignore
            }
            return result;
        }

        private static IEnumerable<string> UninstallLocations()
        {
            var result = new List<string>();
            try
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    foreach (string root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                                                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    {
                        using var key = hive.OpenSubKey(root);
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            using var entry = key.OpenSubKey(sub);
                            if (entry?.GetValue("DisplayName") is not string displayName) continue;
                            bool isVuServer = displayName.Contains("VU", StringComparison.OrdinalIgnoreCase) &&
                                              (displayName.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                                               displayName.Contains("Dials", StringComparison.OrdinalIgnoreCase)) &&
                                              !displayName.Contains("Demo", StringComparison.OrdinalIgnoreCase);
                            if (isVuServer && entry.GetValue("InstallLocation") is string location)
                            {
                                result.Add(location);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Built-in server: registry lookup failed");
            }
            return result;
        }
    }
}
