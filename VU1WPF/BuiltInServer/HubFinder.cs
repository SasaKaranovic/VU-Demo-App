using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Serilog;

namespace VU1WPF.BuiltInServer
{
    /// <summary>Finds the COM port of the VU1 hub (USB VID 0x0403 / PID 0x6015).</summary>
    internal static class HubFinder
    {
        private static readonly Regex ComPortInName = new(@"\((COM\d+)\)", RegexOptions.IgnoreCase);

        public sealed record PortInfo(string Port, string Description, bool IsVu1Hub)
        {
            public override string ToString() => IsVu1Hub ? $"{Port} - VU1 hub" : $"{Port} - {Description}";
        }

        /// <summary>All serial ports, VU1 hubs first.</summary>
        public static List<PortInfo> ListPorts()
        {
            var result = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                foreach (ManagementObject device in searcher.Get())
                {
                    string name = device["Name"] as string ?? "";
                    string pnpId = device["PNPDeviceID"] as string ?? "";
                    var match = ComPortInName.Match(name);
                    if (!match.Success) continue;

                    string port = match.Groups[1].Value.ToUpperInvariant();
                    // FTDI's own driver reports "FTDIBUS\VID_0403+PID_6015...", the Windows driver "USB\VID_0403&PID_6015..."
                    bool isHub = pnpId.Contains("VID_" + HubProtocol.HubVid, StringComparison.OrdinalIgnoreCase) &&
                                 pnpId.Contains("PID_" + HubProtocol.HubPid, StringComparison.OrdinalIgnoreCase);
                    result[port] = new PortInfo(port, ComPortInName.Replace(name, "").Trim(), isHub);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: WMI port lookup failed");
            }

            // Ports WMI didn't describe
            foreach (string port in SafeGetPortNames())
            {
                if (!result.ContainsKey(port)) result[port] = new PortInfo(port, "Serial port", false);
            }

            return result.Values
                .OrderByDescending(p => p.IsVu1Hub)
                .ThenBy(p => p.Port.Length).ThenBy(p => p.Port, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>COM port of the first VU1 hub, or null.</summary>
        public static string? FindHubPort()
        {
            var hub = ListPorts().FirstOrDefault(p => p.IsVu1Hub);
            if (hub != null) Log.Information("Built-in server: found VU1 hub on {Port}", hub.Port);
            else Log.Warning("Built-in server: no VU1 hub (USB VID {Vid} PID {Pid}) found", HubProtocol.HubVid, HubProtocol.HubPid);
            return hub?.Port;
        }

        private static string[] SafeGetPortNames()
        {
            try
            {
                return SerialPort.GetPortNames();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
