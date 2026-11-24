using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using Serilog;

namespace VU1WPF.BuiltInServer
{
    /// <summary>
    /// Serial link to the VU1 hub (C# port of serial_driver.py).
    /// 115200 8N1, ASCII lines terminated by CR/LF. Not thread-safe: the built-in server
    /// uses it from a single worker thread only.
    /// </summary>
    internal sealed class HubSerialPort : IDisposable
    {
        public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(5);

        private readonly SerialPort _port;

        /// <summary>Log every line sent/received (very verbose).</summary>
        public bool DebugUart { get; set; }

        public string PortName => _port.PortName;
        public bool IsOpen => _port.IsOpen;

        public HubSerialPort(string portName)
        {
            _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII,
                NewLine = "\n",
                ReadTimeout = 200,
                WriteTimeout = 2000,
                DtrEnable = false,
                RtsEnable = false,
            };
        }

        /// <summary>Opens the port. Throws UnauthorizedAccessException when another program (e.g. the VU1 server) uses it.</summary>
        public void Open()
        {
            if (_port.IsOpen) return;
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            Log.Information("Built-in server: opened {Port}", _port.PortName);
        }

        public void Close()
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                    _port.Close();
                    Log.Information("Built-in server: closed {Port}", _port.PortName);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: error closing {Port}", _port.PortName);
            }
        }

        /// <summary>
        /// Sends one request line and collects reply lines until one starts with '&lt;' or the timeout expires.
        /// Stale bytes from earlier (timed-out) transactions are discarded first.
        /// Throws IOException/InvalidOperationException when the hub disappears.
        /// </summary>
        public List<string> Transaction(string payload, TimeSpan? replyTimeout = null, bool ignoreReply = false)
        {
            if (!_port.IsOpen) throw new InvalidOperationException("Hub port is not open");

            // Drop leftovers so an old "<..." line is never taken as this command's reply
            if (_port.BytesToRead > 0)
            {
                string stale = _port.ReadExisting();
                if (DebugUart) Log.Debug("UART discard: {Stale}", stale.Trim());
            }

            if (DebugUart) Log.Debug("UART TX: {Payload}", payload);
            _port.Write(payload + "\r\n");

            var lines = new List<string>();
            if (ignoreReply) return lines;

            var timeout = replyTimeout ?? DefaultReplyTimeout;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                string line;
                try
                {
                    line = _port.ReadLine().Trim();
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (line.Length == 0) continue;
                if (DebugUart) Log.Debug("UART RX: {Line}", line);
                lines.Add(line);
                if (line.StartsWith('<')) return lines;
            }

            Log.Warning("Built-in server: no reply to {Payload} within {Ms} ms (received: {Lines})",
                        payload, (int)timeout.TotalMilliseconds, string.Join(" | ", lines));
            return lines;
        }

        public void Dispose()
        {
            Close();
            _port.Dispose();
        }
    }
}
