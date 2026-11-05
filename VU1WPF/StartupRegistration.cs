using System;
using Microsoft.Win32;
using Serilog;

namespace VU1WPF
{
    /// <summary>"Start when I sign in" via HKCU\...\Run.</summary>
    internal static class StartupRegistration
    {
        private const string ValueName = "VU1-Demo-App";
        private const string RunKeyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

        public static bool IsEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(ValueName) != null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read run-on-startup setting");
                return false;
            }
        }

        /// <summary>Throws on failure so the caller can show the error.</summary>
        public static void SetEnabled(bool enabled)
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppPaths.AppDir, "VU1-Demo-App.exe");
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            Log.Information("Run on startup set to {Enabled}", enabled);
        }
    }
}
