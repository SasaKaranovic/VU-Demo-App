using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;

namespace VU1WPF.BuiltInServer
{
    /// <summary>
    /// High-level hub commands (C# port of dial_driver.DialSerialDriver).
    /// Dials are addressed by their bus index. Single-threaded: call only from the server worker.
    /// </summary>
    internal sealed class HubDialDriver : IDisposable
    {
        // Value/backlight writes are ACKed immediately (not gated on easing), so a short wait is enough
        private static readonly TimeSpan FastReplyTimeout = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan ImageChunkDelay = TimeSpan.FromMilliseconds(200);

        private readonly HubSerialPort _port;

        public HubDialDriver(HubSerialPort port)
        {
            _port = port;
        }

        public string PortName => _port.PortName;

        private HubReply Send(int cmd, int dataType, IReadOnlyList<int>? data = null, TimeSpan? timeout = null)
        {
            string payload = HubProtocol.Encode(cmd, dataType, data);
            var lines = _port.Transaction(payload, timeout);
            var reply = HubProtocol.ParseReply(lines, cmd);
            if (!reply.Received)
            {
                Log.Debug("Built-in server: no reply for command 0x{Cmd:X2}", cmd);
            }
            else if (!reply.Success)
            {
                Log.Warning("Built-in server: command 0x{Cmd:X2} failed (reply data '{Data}')", cmd, reply.Data);
            }
            return reply;
        }

        // ------------------------------------------------------------------ discovery

        /// <summary>Rescans the bus and returns (index, uid) of every online dial.</summary>
        public List<(int Index, string Uid)> ScanDials()
        {
            Send(HubProtocol.CMD_RESCAN_BUS, HubProtocol.DATA_NONE);

            var map = Send(HubProtocol.CMD_GET_DEVICES_MAP, HubProtocol.DATA_NONE);
            var result = new List<(int, string)>();
            if (!map.Success)
            {
                Log.Error("Built-in server: invalid reply to GET_DEVICES_MAP");
                return result;
            }

            foreach (int index in HubProtocol.ParseDeviceMap(map.Data))
            {
                var uid = Send(HubProtocol.CMD_GET_DEVICE_UID, HubProtocol.DATA_SINGLE_VALUE, new[] { index });
                if (uid.Success && !uid.IsStatus)
                {
                    result.Add((index, uid.Data.Trim()));
                }
                else
                {
                    Log.Warning("Built-in server: could not read UID of dial #{Index}", index);
                }
            }

            Log.Information("Built-in server: {Count} dial(s) on the bus", result.Count);
            return result;
        }

        /// <summary>Asks the hub to look for new (unprovisioned) dials.</summary>
        public bool ProvisionDials() => Send(HubProtocol.CMD_PROVISION_DEVICE, HubProtocol.DATA_NONE).Success;

        // ------------------------------------------------------------------ value / backlight

        public bool SetPercent(int index, int value) =>
            Send(HubProtocol.CMD_SET_DIAL_PERC_SINGLE, HubProtocol.DATA_KEY_VALUE_PAIR,
                 new[] { index, Math.Clamp(value, 0, 100) }, FastReplyTimeout).Success;

        public bool SetPercentMultiple(IReadOnlyList<(int Index, int Value)> dials)
        {
            if (dials.Count == 0) return true;
            var data = new List<int>(dials.Count * 2);
            foreach (var (index, value) in dials)
            {
                data.Add(index);
                data.Add(Math.Clamp(value, 0, 100));
            }
            return Send(HubProtocol.CMD_SET_DIAL_PERC_MULTIPLE, HubProtocol.DATA_KEY_VALUE_PAIR, data).Success;
        }

        /// <summary>Backlight in percent (0..100) per channel.</summary>
        public bool SetBacklight(int index, int red, int green, int blue, int white) =>
            Send(HubProtocol.CMD_SET_RGB_BACKLIGHT, HubProtocol.DATA_MULTIPLE_VALUE,
                 new[] { index, Math.Clamp(red, 0, 100), Math.Clamp(green, 0, 100), Math.Clamp(blue, 0, 100), Math.Clamp(white, 0, 100) },
                 FastReplyTimeout).Success;

        // ------------------------------------------------------------------ easing

        public void SetEasing(int index, int dialStep, int dialPeriod, int backlightStep, int backlightPeriod)
        {
            Send(HubProtocol.CMD_SET_DIAL_EASING_STEP, HubProtocol.DATA_SINGLE_VALUE, HubProtocol.IndexAndUInt32(index, (uint)Math.Max(0, dialStep)));
            Send(HubProtocol.CMD_SET_DIAL_EASING_PERIOD, HubProtocol.DATA_SINGLE_VALUE, HubProtocol.IndexAndUInt32(index, (uint)Math.Max(0, dialPeriod)));
            Send(HubProtocol.CMD_SET_BACKLIGHT_EASING_STEP, HubProtocol.DATA_SINGLE_VALUE, HubProtocol.IndexAndUInt32(index, (uint)Math.Max(0, backlightStep)));
            Send(HubProtocol.CMD_SET_BACKLIGHT_EASING_PERIOD, HubProtocol.DATA_SINGLE_VALUE, HubProtocol.IndexAndUInt32(index, (uint)Math.Max(0, backlightPeriod)));
        }

        // ------------------------------------------------------------------ e-ink display

        /// <summary>Clears the display, uploads the packed image (see <see cref="DialImagePacker"/>) and shows it.</summary>
        public bool UpdateDisplay(int index, byte[] packedImage, CancellationToken ct)
        {
            Send(HubProtocol.CMD_DISPLAY_CLEAR, HubProtocol.DATA_SINGLE_VALUE, new[] { index, 0 });        // 0 = white background
            Send(HubProtocol.CMD_DISPLAY_GOTO_XY, HubProtocol.DATA_SINGLE_VALUE, new[] { index, 0, 0, 0, 0 });

            for (int offset = 0; offset < packedImage.Length; offset += HubProtocol.ImageChunkSize)
            {
                ct.ThrowIfCancellationRequested();

                int len = Math.Min(HubProtocol.ImageChunkSize, packedImage.Length - offset);
                var data = new int[len + 1];
                data[0] = index;
                for (int i = 0; i < len; i++) data[i + 1] = packedImage[offset + i];

                if (!Send(HubProtocol.CMD_DISPLAY_IMG_DATA, HubProtocol.DATA_SINGLE_VALUE, data).Success)
                {
                    Log.Error("Built-in server: image chunk at offset {Offset} for dial #{Index} failed", offset, index);
                    return false;
                }
                Thread.Sleep(ImageChunkDelay);
            }

            return Send(HubProtocol.CMD_DISPLAY_SHOW_IMG, HubProtocol.DATA_SINGLE_VALUE, new[] { index }).Success;
        }

        public void Dispose() => _port.Dispose();
    }
}
