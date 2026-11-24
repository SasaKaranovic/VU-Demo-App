using System;
using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace VU1WPF
{
    /// <summary>
    /// LibreHardwareMonitor (0.9.5+) reads CPU temperature/power/clocks and motherboard sensors
    /// through the signed PawnIO driver (it replaced WinRing0, which Windows Defender flags).
    /// PawnIO is installed separately; this class detects it and opens the download page.
    /// </summary>
    internal static class PawnIoCheck
    {
        public const string DownloadUrl = "https://pawnio.eu/";

        public const string InstallSteps =
            "PawnIO is a small, signed Windows driver that LibreHardwareMonitor uses to read CPU temperature, " +
            "CPU power and clocks, and motherboard sensors.\n\n" +
            "How to install:\n" +
            "  1. Click \"Download PawnIO\" (opens pawnio.eu).\n" +
            "  2. Download the latest PawnIO setup (version 2.1 or newer).\n" +
            "  3. Run it and accept the administrator prompt.\n" +
            "  4. Restart VU1 Demo App.\n\n" +
            "Load, memory, network, storage and most GPU sensors work without it.";

        /// <summary>True when the PawnIO driver service or its installer entry is present.</summary>
        public static bool IsInstalled()
        {
            try
            {
                using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                {
                    if (service != null) return true;
                }

                foreach (string root in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                })
                {
                    using var uninstall = Registry.LocalMachine.OpenSubKey(root);
                    if (uninstall == null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is string displayName &&
                            displayName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not check for PawnIO");
                return true;   // don't nag when we can't tell
            }

            return false;
        }

        public static void OpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open {Url}", DownloadUrl);
            }
        }
    }
}
