using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KR_VU1_Server;
using Serilog;

namespace VU1WPF.BuiltInServer
{
    /// <summary>
    /// Built-in replacement for the external VU1 server: talks to the VU1 hub directly over USB.
    /// C# port of the relevant parts of the VU1 server (server_dial_handler.py + dial_driver.py),
    /// without the HTTP/REST API.
    ///
    /// - All serial I/O runs on one dedicated worker thread.
    /// - Value / backlight / image requests are queued and sent by the worker every 200 ms
    ///   (like the VU1 server's periodic update), with retry back-off for unresponsive dials.
    /// - The worker (re)connects automatically, e.g. after the hub is unplugged and plugged back in.
    /// - Dial names, easing settings and images are stored in the app's data folder and imported
    ///   from an installed VU1 server on every start (see <see cref="ExternalServerImporter"/>).
    /// </summary>
    internal sealed class BuiltInDialServer : IDialBackend
    {
        private static readonly TimeSpan UpdatePeriod = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ImageUploadTimeout = TimeSpan.FromSeconds(60);

        // Same retry policy as the VU1 server (ServerDialHandler)
        private const int MaxDeliveryFailures = 5;
        private const double BackoffBaseSeconds = 1.0;
        private const double BackoffMaxSeconds = 30.0;

        private sealed class DialState
        {
            public int Index;
            public string Uid = "";
            public string Name = "";

            public int Value;
            public bool ValueChanged;
            public int ValueFailures;
            public DateTime ValueRetryAfter;

            public (int r, int g, int b, int w) Backlight;
            public bool BacklightChanged;
            public int BacklightFailures;
            public DateTime BacklightRetryAfter;

            public byte[]? PendingImage;
            public TaskCompletionSource<bool>? PendingImageDone;
        }

        private readonly object _sync = new();
        private readonly Dictionary<string, DialState> _dials = new();
        private readonly BuiltInStore _store = new();
        private readonly string? _configuredPort;
        private readonly string? _externalFolder;
        private readonly Thread _worker;
        private readonly AutoResetEvent _wake = new(false);
        private readonly CancellationTokenSource _stop = new();

        private HubDialDriver? _driver;
        private volatile bool _online;
        private int _failures;
        private volatile string? _portName;
        private volatile string? _lastError;

        public event Action<bool>? ConnectionStateChanged;

        /// <param name="hubPort">COM port of the hub, or null/empty to detect it.</param>
        /// <param name="externalServerFolder">Installed VU1 server to import data from, or null/empty to detect it.</param>
        public BuiltInDialServer(string? hubPort, string? externalServerFolder)
        {
            _configuredPort = string.IsNullOrWhiteSpace(hubPort) ? null : hubPort.Trim();
            _externalFolder = string.IsNullOrWhiteSpace(externalServerFolder) ? null : externalServerFolder.Trim();

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VU1 built-in server" };
            _worker.Start();
        }

        // ================================================================== IDialBackend

        public bool IsBuiltIn => true;
        public bool IsOnline => _online;
        public int ConsecutiveFailures => Volatile.Read(ref _failures);

        /// <summary>Why the server is offline (hub not found, port in use, ...), or null.</summary>
        public string? LastError => _lastError;

        public string ServerUrl => _portName != null ? $"Built-in server · {_portName}" : "Built-in server";

        public Task<bool> RefreshDialListAsync(CancellationToken ct = default) => Task.FromResult(_online);

        public List<DialInfo> GetDialList()
        {
            lock (_sync)
            {
                return _dials.Values
                    .OrderBy(d => d.Index)
                    .Select(d => new DialInfo
                    {
                        uid = d.Uid,
                        dial_name = d.Name,
                        value = d.Value,
                        backlight = new Backlight { red = d.Backlight.r, green = d.Backlight.g, blue = d.Backlight.b },
                        image_file = null,
                    })
                    .ToList();
            }
        }

        public Task<bool> UpdateDialNameAsync(string uid, string dialName, CancellationToken ct = default)
        {
            dialName = (dialName ?? "").Trim();
            if (dialName.Length == 0 || dialName.Length > 30) return Task.FromResult(false);

            lock (_sync)
            {
                if (!_dials.TryGetValue(uid, out var dial)) return Task.FromResult(false);
                dial.Name = dialName;
            }
            _store.Update(uid, d => d.Name = dialName);
            return Task.FromResult(true);
        }

        public Task<bool> UpdateDialValueAsync(string uid, int val, CancellationToken ct = default)
        {
            val = Math.Clamp(val, 0, 100);
            lock (_sync)
            {
                if (!_online || !_dials.TryGetValue(uid, out var dial)) return Task.FromResult(false);

                // A new request always gets a clean attempt (clears back-off / unresponsive state)
                dial.Value = val;
                dial.ValueChanged = true;
                dial.ValueFailures = 0;
                dial.ValueRetryAfter = DateTime.MinValue;
            }
            _wake.Set();
            return Task.FromResult(true);
        }

        public Task<bool> UpdateDialBacklightAsync(string uid, int red, int green, int blue, bool values_as_percent = false, CancellationToken ct = default)
        {
            // Same scaling as the HTTP client, so colours look identical in both modes
            var (r, g, b) = VU1_Server.ScaleBacklight(red, green, blue, values_as_percent);
            lock (_sync)
            {
                if (!_online || !_dials.TryGetValue(uid, out var dial)) return Task.FromResult(false);

                dial.Backlight = (r, g, b, 0);
                dial.BacklightChanged = true;
                dial.BacklightFailures = 0;
                dial.BacklightRetryAfter = DateTime.MinValue;
            }
            _wake.Set();
            return Task.FromResult(true);
        }

        public async Task<bool> UploadDialImageAsync(string uid, byte[] imageBytes, string fileName = "dial.png", CancellationToken ct = default)
        {
            if (imageBytes == null || imageBytes.Length == 0) return false;

            byte[] packed;
            try
            {
                packed = await Task.Run(() => DialImagePacker.Pack(imageBytes), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Built-in server: could not convert image for dial {Uid}", uid);
                return false;
            }

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (!_online || !_dials.TryGetValue(uid, out var dial)) return false;

                dial.PendingImageDone?.TrySetResult(false);   // superseded by this image
                dial.PendingImage = packed;
                dial.PendingImageDone = done;
            }
            _store.SaveImage(uid, imageBytes);
            _wake.Set();

            try
            {
                return await done.Task.WaitAsync(ImageUploadTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warning("Built-in server: image upload to {Uid} timed out", uid);
                return false;
            }
        }

        public Task<byte[]?> GetDialImageAsync(string uid, CancellationToken ct = default) =>
            Task.FromResult(_store.LoadImage(uid));

        // ================================================================== worker

        private void WorkerLoop()
        {
            var ct = _stop.Token;

            // Bring over names / images from an installed VU1 server (only what changed since last time)
            try
            {
                ExternalServerImporter.Import(_store, _externalFolder);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Built-in server: import failed");
            }

            TimeSpan reconnectDelay = TimeSpan.FromSeconds(2);

            while (!ct.IsCancellationRequested)
            {
                if (!_online)
                {
                    if (TryConnect())
                    {
                        reconnectDelay = TimeSpan.FromSeconds(2);
                    }
                    else
                    {
                        if (ct.WaitHandle.WaitOne(reconnectDelay)) break;
                        reconnectDelay = TimeSpan.FromTicks(Math.Min(reconnectDelay.Ticks * 2, MaxReconnectDelay.Ticks));
                        continue;
                    }
                }

                try
                {
                    PeriodicUpdate(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
                {
                    Log.Error(ex, "Built-in server: lost connection to the hub");
                    Disconnect("Lost connection to the VU1 hub: " + ex.Message);
                    continue;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Built-in server: error in update loop");
                }

                WaitHandle.WaitAny(new[] { ct.WaitHandle, _wake }, UpdatePeriod);
            }

            // Shutting down: deliver what's still queued (e.g. "all dials to 0") and close the port
            if (_online)
            {
                try
                {
                    PeriodicUpdate(CancellationToken.None, includeImages: false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Built-in server: final update failed");
                }
            }
            Disconnect(null, raiseEvent: false);
            Log.Information("Built-in server stopped");
        }

        private bool TryConnect()
        {
            string? port = _configuredPort ?? HubFinder.FindHubPort();
            if (port == null)
            {
                Fail("VU1 hub not found. Check that it is plugged in (and the FTDI driver is installed).");
                return false;
            }

            HubDialDriver? driver = null;
            try
            {
                var serial = new HubSerialPort(port);
                serial.Open();
                driver = new HubDialDriver(serial);
                _portName = port;

                var found = driver.ScanDials();
                if (found.Count == 0)
                {
                    Log.Information("Built-in server: no dials found, searching the bus for new ones...");
                    for (int i = 0; i < 3; i++)
                    {
                        driver.ProvisionDials();
                        Thread.Sleep(200);
                    }
                    found = driver.ScanDials();
                }

                var states = new Dictionary<string, DialState>();
                foreach (var (index, uid) in found)
                {
                    if (string.IsNullOrEmpty(uid) || states.ContainsKey(uid)) continue;
                    var stored = _store.GetOrCreate(uid);

                    // Restore the dial's behaviour settings, like the VU1 server does on start-up
                    driver.SetEasing(index, stored.EasingDialStep, stored.EasingDialPeriod,
                                     stored.EasingBacklightStep, stored.EasingBacklightPeriod);

                    states[uid] = new DialState
                    {
                        Index = index,
                        Uid = uid,
                        Name = stored.Name,
                        Value = 0,
                        Backlight = (0, 0, 0, 0),
                        BacklightChanged = true,   // start dark, like the VU1 server
                    };
                }

                driver.SetPercentMultiple(states.Values.Select(d => (d.Index, 0)).ToList());

                lock (_sync)
                {
                    foreach (var old in _dials.Values) old.PendingImageDone?.TrySetResult(false);
                    _dials.Clear();
                    foreach (var kv in states) _dials[kv.Key] = kv.Value;
                }

                _driver = driver;
                _lastError = null;
                Interlocked.Exchange(ref _failures, 0);
                _online = true;
                Log.Information("Built-in server running on {Port} with {Count} dial(s)", port, states.Count);
                ConnectionStateChanged?.Invoke(true);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                driver?.Dispose();
                Fail($"{port} is in use by another program (is the VU1 server running?).");
                return false;
            }
            catch (Exception ex)
            {
                driver?.Dispose();
                Log.Error(ex, "Built-in server: could not start on {Port}", port);
                Fail($"Could not open the VU1 hub on {port}: {ex.Message}");
                return false;
            }
        }

        private void Fail(string message)
        {
            int failures = Interlocked.Increment(ref _failures);
            if (_lastError != message || failures == 1)
            {
                Log.Warning("Built-in server: {Message}", message);
            }
            _lastError = message;
            ConnectionStateChanged?.Invoke(false);
        }

        private void Disconnect(string? reason, bool raiseEvent = true)
        {
            bool wasOnline = _online;
            _online = false;
            _driver?.Dispose();
            _driver = null;

            lock (_sync)
            {
                foreach (var d in _dials.Values)
                {
                    d.PendingImageDone?.TrySetResult(false);
                    d.PendingImage = null;
                    d.PendingImageDone = null;
                }
            }

            if (reason != null) _lastError = reason;
            if (wasOnline && raiseEvent)
            {
                Interlocked.Increment(ref _failures);
                ConnectionStateChanged?.Invoke(false);
            }
        }

        /// <summary>Sends queued values, backlights and images (port of ServerDialHandler.periodic_dial_update).</summary>
        private void PeriodicUpdate(CancellationToken ct, bool includeImages = true)
        {
            var driver = _driver;
            if (driver == null) return;

            List<DialState> dials;
            lock (_sync) dials = _dials.Values.ToList();

            DateTime now = DateTime.UtcNow;

            // --- values
            foreach (var dial in dials)
            {
                int value;
                lock (_sync)
                {
                    if (!dial.ValueChanged || Blocked(dial.ValueFailures, dial.ValueRetryAfter, now)) continue;
                    value = dial.Value;
                }

                bool ok = driver.SetPercent(dial.Index, value);
                lock (_sync)
                {
                    if (ok)
                    {
                        if (dial.Value == value) dial.ValueChanged = false;   // keep newer requests pending
                        dial.ValueFailures = 0;
                    }
                    else
                    {
                        NoteFailure(ref dial.ValueFailures, ref dial.ValueRetryAfter, now, dial.Uid, "value");
                    }
                }
            }

            // --- backlight
            foreach (var dial in dials)
            {
                (int r, int g, int b, int w) colour;
                lock (_sync)
                {
                    if (!dial.BacklightChanged || Blocked(dial.BacklightFailures, dial.BacklightRetryAfter, now)) continue;
                    colour = dial.Backlight;
                }

                bool ok = driver.SetBacklight(dial.Index, colour.r, colour.g, colour.b, colour.w);
                lock (_sync)
                {
                    if (ok)
                    {
                        if (dial.Backlight == colour) dial.BacklightChanged = false;
                        dial.BacklightFailures = 0;
                    }
                    else
                    {
                        NoteFailure(ref dial.BacklightFailures, ref dial.BacklightRetryAfter, now, dial.Uid, "backlight");
                    }
                }
            }

            // --- images (slow: ~1 s per image)
            if (!includeImages) return;
            foreach (var dial in dials)
            {
                byte[]? image;
                TaskCompletionSource<bool>? done;
                lock (_sync)
                {
                    image = dial.PendingImage;
                    done = dial.PendingImageDone;
                    dial.PendingImage = null;
                    dial.PendingImageDone = null;
                }
                if (image == null) continue;

                try
                {
                    Log.Information("Built-in server: sending image to dial {Uid}", dial.Uid);
                    bool ok = driver.UpdateDisplay(dial.Index, image, ct);
                    done?.TrySetResult(ok);
                }
                catch (Exception)
                {
                    done?.TrySetResult(false);
                    throw;
                }
            }
        }

        private static bool Blocked(int failures, DateTime retryAfter, DateTime now) =>
            failures >= MaxDeliveryFailures || now < retryAfter;

        private static void NoteFailure(ref int failures, ref DateTime retryAfter, DateTime now, string uid, string kind)
        {
            failures++;
            if (failures >= MaxDeliveryFailures)
            {
                Log.Error("Built-in server: dial {Uid} unresponsive after {Count} {Kind} attempts; waiting for a new request",
                          uid, failures, kind);
                return;
            }
            double backoff = Math.Min(BackoffBaseSeconds * Math.Pow(2, failures - 1), BackoffMaxSeconds);
            retryAfter = now.AddSeconds(backoff);
            Log.Warning("Built-in server: {Kind} update for dial {Uid} failed; retrying in {Seconds}s", kind, uid, backoff);
        }

        public void Dispose()
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            _wake.Set();
            if (!_worker.Join(TimeSpan.FromSeconds(5)))
            {
                Log.Warning("Built-in server: worker did not stop in time");
            }
        }
    }
}
