using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VU1WPF.BuiltInServer
{
    /// <summary>
    /// VU1 hub serial protocol (C# port of the VU1 server's dials/Comms_Hub_Server.py and the
    /// command encoding in dial_driver.py).
    ///
    /// Request:  ">" CC TT LLLL DATA "\r\n"   (hex, CC=command, TT=data type, LLLL=data length in bytes)
    /// Reply:    "<" CC TT LLLL DATA          (one line; TT=05 means DATA is a 16-bit status code)
    /// </summary>
    public static class HubProtocol
    {
        // ---- hub commands (hub_commands)
        public const int CMD_SET_DIAL_RAW_SINGLE = 0x01;
        public const int CMD_SET_DIAL_PERC_SINGLE = 0x03;
        public const int CMD_SET_DIAL_PERC_MULTIPLE = 0x04;
        public const int CMD_SET_DIAL_CALIBRATE_MAX = 0x05;
        public const int CMD_SET_DIAL_CALIBRATE_HALF = 0x06;
        public const int CMD_GET_DEVICES_MAP = 0x07;
        public const int CMD_PROVISION_DEVICE = 0x08;
        public const int CMD_RESET_ALL_DEVICES = 0x09;
        public const int CMD_DIAL_POWER = 0x0A;
        public const int CMD_GET_DEVICE_UID = 0x0B;
        public const int CMD_RESCAN_BUS = 0x0C;
        public const int CMD_DISPLAY_CLEAR = 0x0D;
        public const int CMD_DISPLAY_GOTO_XY = 0x0E;
        public const int CMD_DISPLAY_IMG_DATA = 0x0F;
        public const int CMD_DISPLAY_SHOW_IMG = 0x10;
        public const int CMD_RX_BUFFER_SIZE = 0x11;
        public const int CMD_SET_RGB_BACKLIGHT = 0x13;
        public const int CMD_SET_DIAL_EASING_STEP = 0x14;
        public const int CMD_SET_DIAL_EASING_PERIOD = 0x15;
        public const int CMD_SET_BACKLIGHT_EASING_STEP = 0x16;
        public const int CMD_SET_BACKLIGHT_EASING_PERIOD = 0x17;
        public const int CMD_GET_EASING_CONFIG = 0x18;
        public const int CMD_GET_BUILD_INFO = 0x19;
        public const int CMD_GET_FW_INFO = 0x20;
        public const int CMD_GET_HW_INFO = 0x21;
        public const int CMD_GET_PROTOCOL_INFO = 0x22;

        // ---- data types (hub_data_types)
        public const int DATA_NONE = 0x01;
        public const int DATA_SINGLE_VALUE = 0x02;
        public const int DATA_MULTIPLE_VALUE = 0x03;
        public const int DATA_KEY_VALUE_PAIR = 0x04;
        public const int DATA_STATUS_CODE = 0x05;

        public const int STATUS_OK = 0x0000;

        /// <summary>USB IDs of the VU1 hub (FTDI FT230X): VID 0x0403 (1027), PID 0x6015 (24597).</summary>
        public const string HubVid = "0403";
        public const string HubPid = "6015";

        /// <summary>Image data is sent in chunks of this many bytes.</summary>
        public const int ImageChunkSize = 1000;

        /// <summary>
        /// Builds a request line (without the trailing CR/LF). Values &lt; 256 are sent as one byte,
        /// larger values as two bytes - exactly like dial_driver._sendCommand.
        /// </summary>
        public static string Encode(int cmd, int dataType, IReadOnlyList<int>? data = null)
        {
            var sb = new StringBuilder(">");
            sb.Append(cmd.ToString("X2")).Append(dataType.ToString("X2"));

            if (data == null || data.Count == 0)
            {
                sb.Append("0000");
                return sb.ToString();
            }

            var payload = new StringBuilder(data.Count * 2);
            foreach (int value in data)
            {
                if (value < 0 || value > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(data), value, "Value must fit in 16 bits");
                payload.Append(value < 256 ? value.ToString("X2") : value.ToString("X4"));
            }

            sb.Append((payload.Length / 2).ToString("X4")).Append(payload);
            return sb.ToString();
        }

        /// <summary>Command with a dial index followed by a big-endian 32-bit value.</summary>
        public static int[] IndexAndUInt32(int index, uint value) =>
            new[] { index, (int)((value >> 24) & 0xFF), (int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF) };

        /// <summary>
        /// Finds the reply to <paramref name="expectedCmd"/> among the received lines.
        /// Replies for other commands (stale) are skipped.
        /// </summary>
        public static HubReply ParseReply(IEnumerable<string> lines, int expectedCmd)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith('<') || line.Length < 9) continue;

                if (!TryHex(line.Substring(1, 2), out int cmd) || cmd != expectedCmd) continue;
                if (!TryHex(line.Substring(3, 2), out int dataType)) continue;

                string data = line.Length > 9 ? line.Substring(9) : "";
                if (dataType == DATA_STATUS_CODE)
                {
                    bool ok = TryHex(data, out int status) && status == STATUS_OK;
                    return new HubReply(true, ok, data, isStatus: true);
                }
                return new HubReply(true, data.Length > 0, data, isStatus: false);
            }
            return HubReply.NoReply;
        }

        public static bool TryHex(string hex, out int value) =>
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        /// <summary>Decodes a hex string ("414243") to text ("ABC"); empty on error.</summary>
        public static string HexToText(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return "";
            try
            {
                return Encoding.UTF8.GetString(Convert.FromHexString(hex)).TrimEnd('\0');
            }
            catch (FormatException)
            {
                return "";
            }
        }

        /// <summary>"0100010000..." from CMD_GET_DEVICES_MAP -> indexes of online dials.</summary>
        public static List<int> ParseDeviceMap(string data)
        {
            var online = new List<int>();
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (TryHex(data.Substring(i, 2), out int v) && v == 1) online.Add(i / 2);
            }
            return online;
        }
    }

    /// <summary>Parsed hub reply.</summary>
    public readonly struct HubReply
    {
        public static readonly HubReply NoReply = new(false, false, "", false);

        public HubReply(bool received, bool success, string data, bool isStatus)
        {
            Received = received;
            Success = success;
            Data = data;
            IsStatus = isStatus;
        }

        /// <summary>A reply for the command was received.</summary>
        public bool Received { get; }
        /// <summary>Status OK, or a non-empty data payload.</summary>
        public bool Success { get; }
        public string Data { get; }
        public bool IsStatus { get; }
    }
}
