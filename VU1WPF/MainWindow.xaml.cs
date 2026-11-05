using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KR_VU1_ConfigurationManager;
using KR_VU1_Server;
using Serilog;
using static KR_VU1_Sensors.ClassVUSensors;

namespace VU1WPF
{
    /// <summary>
    /// Main window: a grid of dial cards (4 per row), a status bar and the background
    /// loop that pushes sensor values to the VU1 server.
    /// </summary>
    public partial class MainWindow : Window, IDialHost
    {
        // How often a value/backlight is re-sent even if unchanged (recovers from server/dial restarts).
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
        // How often to re-check the dial list while connected (picks up newly attached dials).
        private static readonly TimeSpan DialListRefreshInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(30);
        // Failures before the status turns from yellow (connecting) to red (unreachable)
        private const int FailuresBeforeOffline = 3;

        public ClassConfigurationManager ConfigManager;
        private readonly VU1_SensorManager SensorManager;
        private volatile VU1_Server _server;

        // UI collection (UI thread only) and an immutable snapshot for the background loop
        private readonly ObservableCollection<DialViewModel> _dialVms = new();
        private volatile List<ClassDialGUI> _dials = new();

        // Metric drop-down entries, shared by all dial cards
        private readonly List<SensorOption> _sensorOptions;
        private readonly ListCollectionView _sensorOptionsView;

        private readonly SharedUiState _ui = new();
        private volatile bool _paused;
        private readonly CancellationTokenSource _shutdownCts = new();
        private CancellationTokenSource? _loopCts;
        private Task? _updateLoop;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isClosing;
        private bool _hasConnectedOnce;

        public MainWindow()
        {
            InitializeComponent();

            ConfigManager = new ClassConfigurationManager();
            _server = CreateServer();

            SensorManager = new VU1_SensorManager();
            _sensorOptions = new List<SensorOption> { SensorOption.None };
            _sensorOptions.AddRange(SensorManager.get_used_sensors()
                .Select(s => new SensorOption(s.Sensor))
                .OrderBy(o => o.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Hardware, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase));
            _sensorOptionsView = new ListCollectionView(_sensorOptions);
            _sensorOptionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorOption.Category)));

            _ui.MetricsExpanded = ConfigManager.GetMetricsExpanded();
            DialsHost.ItemsSource = _dialVms;
            txtVersion.Text = Properties.Resources.BuildDate.Trim();

            CreateTrayIcon();
            UpdateStatus();

            Loaded += MainWindow_Loaded;
            SizeChanged += (s, e) => KeepOnScreen();
        }

        private VU1_Server CreateServer()
        {
            var server = new VU1_Server(ConfigManager.GetServerUrl(), ConfigManager.GetMasterKey());
            server.ConnectionStateChanged += online => Dispatcher.InvokeAsync(() => OnConnectionStateChanged(server, online));
            return server;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Rows of dials grow the window (SizeToContent) up to ~90% of the screen, then scroll.
            DialScroller.MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height * 0.9 - 40 - 38 - 2);

            if (ConfigManager.LoadWarning != null)
            {
                MessageBox.Show(this, ConfigManager.LoadWarning, "VU1 Demo App", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            await RefreshDialListAsync();
            StartUpdateLoop();
        }

        private void KeepOnScreen()
        {
            Rect area = SystemParameters.WorkArea;
            if (Top + ActualHeight > area.Bottom)
            {
                Top = Math.Max(area.Top, area.Bottom - ActualHeight);
            }
        }

        // ================================================================== tray icon / window chrome

        private void CreateTrayIcon()
        {
            try
            {
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open", null, (s, e) => Dispatcher.Invoke(RestoreFromTray));
                menu.Items.Add("Exit", null, (s, e) => Dispatcher.Invoke(() => Application.Current.Shutdown()));

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    // Absolute path: the working directory is not the app folder when started on boot.
                    Icon = new System.Drawing.Icon(AppPaths.IconIco),
                    Text = "VU1 Demo App",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                _notifyIcon.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreFromTray);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create system tray icon");
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void btnX_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        private void btnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void btnMinToTray_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
            Hide();
        }

        // ================================================================== status bar

        private void OnConnectionStateChanged(VU1_Server source, bool online)
        {
            if (!ReferenceEquals(source, _server)) return;   // event from a replaced server instance
            if (online) _hasConnectedOnce = true;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var server = _server;
            string url = server.ServerUrl;
            string detail;
            Color color;
            string title;

            if (server.IsOnline)
            {
                color = (Color)FindResource("OkColor");
                title = _paused ? "Connected · paused" : "Connected";
                int count = _dialVms.Count;
                detail = $"{url}  ·  {count} dial{(count == 1 ? "" : "s")}  ·  updating every {ConfigManager.GetDialUpdatePeriod().ToString("0.##", CultureInfo.InvariantCulture)} s";
            }
            else if (server.ConsecutiveFailures < FailuresBeforeOffline)
            {
                color = (Color)FindResource("WarnColor");
                title = _hasConnectedOnce ? "Reconnecting…" : "Connecting…";
                detail = url;
            }
            else
            {
                color = (Color)FindResource("DangerColor");
                title = "Server unreachable";
                detail = $"{url}  ·  retrying";
            }

            StatusDot.Fill = new SolidColorBrush(color);
            StatusGlow.Color = color;
            txtServerStatus.Text = title;
            txtServerDetail.Text = detail;

            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = _dialVms.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            DialScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            if (empty)
            {
                if (_server.IsOnline)
                {
                    txtEmptyTitle.Text = "No dials found";
                    txtEmptyDetail.Text = "The VU1 server is running but reports no dials. Check that the dials are connected to the hub.";
                }
                else
                {
                    txtEmptyTitle.Text = "Can't reach the VU1 server";
                    txtEmptyDetail.Text = $"Make sure the VU1 server is running at {_server.ServerUrl} and the API key is correct.";
                }
            }
        }

        // ================================================================== dial list

        private async Task RefreshDialListAsync()
        {
            Log.Information("Refreshing dial list");
            btnRefreshDials.IsEnabled = false;
            try
            {
                var server = _server;
                if (await server.RefreshDialListAsync(_shutdownCts.Token))
                {
                    ApplyDialList(server.GetDialList(), fetchAllImages: true);
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            finally
            {
                btnRefreshDials.IsEnabled = true;
                UpdateStatus();
            }
        }

        /// <summary>Synchronises the dial cards with the server's dial list. UI thread only.</summary>
        private void ApplyDialList(List<DialInfo> serverDials, bool fetchAllImages)
        {
            var incoming = serverDials.Where(d => d != null && !string.IsNullOrEmpty(d.uid))
                                      .GroupBy(d => d.uid).Select(g => g.First())
                                      .ToList();
            var incomingUids = new HashSet<string>(incoming.Select(d => d.uid));

            // Remove dials that disappeared
            foreach (var vm in _dialVms.Where(v => !incomingUids.Contains(v.Uid)).ToList())
            {
                _dialVms.Remove(vm);
            }

            // Add new dials / refresh names of existing ones
            var newVms = new List<DialViewModel>();
            foreach (var info in incoming)
            {
                var existing = _dialVms.FirstOrDefault(v => v.Uid == info.uid);
                string name = string.IsNullOrWhiteSpace(info.dial_name) ? info.uid : info.dial_name;
                if (existing != null)
                {
                    existing.Dial.FriendlyName = name;
                    existing.Name = name;
                    continue;
                }

                var vm = CreateDialViewModel(info, name);
                _dialVms.Add(vm);
                newVms.Add(vm);
            }

            // Order: as arranged by the user (drag & drop), new dials after that sorted by name
            var savedOrder = ConfigManager.GetDialOrder();
            var sorted = _dialVms
                .OrderBy(v => { int i = savedOrder.IndexOf(v.Uid); return i < 0 ? int.MaxValue : i; })
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = _dialVms.IndexOf(sorted[i]);
                if (current != i) _dialVms.Move(current, i);
            }

            _dials = _dialVms.Select(v => v.Dial).ToList();

            foreach (var vm in fetchAllImages ? _dialVms.ToList() : newVms)
            {
                _ = RefreshDialImageAsync(vm);
            }

            Log.Information("Dial list updated: {Count} dial(s)", _dialVms.Count);
            UpdateStatus();
        }

        private DialViewModel CreateDialViewModel(DialInfo info, string name)
        {
            var dial = new ClassDialGUI
            {
                FriendlyName = name,
                UID = info.uid,
                Finish = ConfigManager.GetDialFinish(info.uid),
                RulesEnabled = ConfigManager.GetDialRulesEnabled(info.uid),
                FixedColor = ConfigManager.GetDialFixedColor(info.uid),
            };
            dial.ScaleMin = ConfigManager.GetDialMin(info.uid);
            dial.ScaleMax = ConfigManager.GetDialMax(info.uid);
            dial.Thresholds = ConfigManager.GetDialThresholds(info.uid);

            string sensorId = ConfigManager.GetDialMetric(info.uid);
            dial.SensorIdentifier = sensorId;

            SensorOption option = SensorOption.None;
            if (!string.IsNullOrEmpty(sensorId))
            {
                option = _sensorOptions.FirstOrDefault(o => o.Identifier == sensorId) ?? SensorOption.None;
                if (option.Sensor != null)
                {
                    dial.Sensor = option.Sensor;
                    dial.SensorName = option.Name;
                }
                else
                {
                    Log.Warning("Sensor {Sensor} configured for dial {Uid} was not found on this computer", sensorId, info.uid);
                }
            }

            if (info.backlight != null)
            {
                dial.ServerBacklight = ((int)Math.Round(info.backlight.red), (int)Math.Round(info.backlight.green), (int)Math.Round(info.backlight.blue));
            }

            var vm = new DialViewModel(dial, this, _sensorOptionsView, _ui) { SelectedSensorOption = option };
            if (!dial.UsesRules && dial.FixedColor.HasValue)
            {
                var (r, g, b) = dial.FixedColor.Value;
                vm.SetBacklight(r, g, b);
            }
            else if (info.backlight != null)
            {
                vm.SetBacklight((int)Math.Round(info.backlight.red), (int)Math.Round(info.backlight.green), (int)Math.Round(info.backlight.blue));
            }
            return vm;
        }

        private async Task RefreshDialImageAsync(DialViewModel vm)
        {
            try
            {
                byte[]? bytes = await _server.GetDialImageAsync(vm.Uid, _shutdownCts.Token);
                vm.SetDisplayImage(bytes);   // null -> blank
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not fetch image for dial {Uid}", vm.Uid);
                vm.SetDisplayImage(null);
            }
        }

        // ================================================================== background update loop

        private void StartUpdateLoop()
        {
            _loopCts?.Dispose();
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var ct = _loopCts.Token;
            float period = ConfigManager.GetDialUpdatePeriod();
            _updateLoop = Task.Run(() => DialUpdateLoopAsync(TimeSpan.FromSeconds(period), ct));
            Log.Information("Dials updated every {Period} seconds.", period);
        }

        private async Task StopUpdateLoopAsync()
        {
            _loopCts?.Cancel();
            if (_updateLoop != null)
            {
                try { await _updateLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* cancelled / timed out */ }
            }
            _updateLoop = null;
        }

        private async Task DialUpdateLoopAsync(TimeSpan period, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(period);

            TimeSpan backoff = TimeSpan.FromSeconds(1);
            DateTime nextServerCheck = DateTime.MinValue;

            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        var server = _server;
                        DateTime now = DateTime.UtcNow;

                        // --- (Re)connect when offline, and periodically refresh the dial list ---
                        if (!server.IsOnline || now >= nextServerCheck)
                        {
                            if (!server.IsOnline && now < nextServerCheck)
                            {
                                continue;
                            }

                            bool wasOnline = server.IsOnline;
                            bool ok = await server.RefreshDialListAsync(ct).ConfigureAwait(false);
                            _ = Dispatcher.InvokeAsync(UpdateStatus);

                            if (!ok)
                            {
                                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxReconnectBackoff.Ticks));
                                nextServerCheck = now + backoff;
                                continue;
                            }

                            backoff = TimeSpan.FromSeconds(1);
                            nextServerCheck = now + DialListRefreshInterval;

                            var serverDials = server.GetDialList();
                            bool listChanged = !serverDials.Select(d => d.uid).OrderBy(u => u)
                                                .SequenceEqual(_dials.Select(d => d.UID).OrderBy(u => u));
                            if (listChanged || !wasOnline)
                            {
                                // Re-fetch images after a reconnect: the server may have restarted
                                await Dispatcher.InvokeAsync(() => ApplyDialList(serverDials, fetchAllImages: !wasOnline));
                            }

                            if (!wasOnline)
                            {
                                foreach (var d in _dials) d.ResetSentState();
                            }
                        }

                        if (_paused) continue;

                        await UpdateDialsOnceAsync(server, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Never let one bad iteration kill the loop
                        Log.Error(ex, "Error in dial update loop");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }

            Log.Debug("Dial update loop stopped");
        }

        private async Task UpdateDialsOnceAsync(VU1_Server server, CancellationToken ct)
        {
            var dials = _dials;  // snapshot
            var active = dials.Where(d => d.Sensor != null).ToList();

            // Dials without a metric can still have a fixed backlight colour
            foreach (var dial in dials.Where(d => d.Sensor == null))
            {
                await SendBacklightIfNeededAsync(server, dial, dial.DesiredBacklight(0), ct).ConfigureAwait(false);
                if (!server.IsOnline) return;
            }

            if (active.Count == 0)
            {
                _ = Dispatcher.InvokeAsync(() => { foreach (var vm in _dialVms) vm.RefreshLive(); });
                return;
            }

            // Update each piece of hardware once, not once per dial
            SensorManager.UpdateSensors(active.Select(d => d.Sensor!));

            foreach (var dial in active)
            {
                ct.ThrowIfCancellationRequested();

                var sensor = dial.Sensor;
                if (sensor == null) continue;

                float raw = sensor.Value ?? 0;
                int dialValue = DialMath.ScaleToPercent(raw, dial.ScaleMin, dial.ScaleMax);
                dial.Value = dialValue;
                DateTime now = DateTime.UtcNow;

                // Only send when changed (or periodically as keep-alive)
                if (dialValue != dial.LastSentValue || now - dial.LastValueSentUtc > KeepAliveInterval)
                {
                    Log.Verbose("Dial:{Uid} set to {Value}% [Sensor: {Sensor}] - Raw value: {Raw}", dial.UID, dialValue, sensor.Identifier, raw);
                    if (await server.UpdateDialValueAsync(dial.UID, dialValue, ct).ConfigureAwait(false))
                    {
                        dial.LastSentValue = dialValue;
                        dial.LastValueSentUtc = now;
                    }
                }

                // Backlight: colour rules (when on) or the fixed colour
                await SendBacklightIfNeededAsync(server, dial, dial.DesiredBacklight(dialValue), ct).ConfigureAwait(false);

                // Don't hammer an unreachable server with the remaining dials
                if (!server.IsOnline) break;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                foreach (var vm in _dialVms) vm.RefreshLive();
            });
        }

        private static async Task SendBacklightIfNeededAsync(VU1_Server server, ClassDialGUI dial, (int r, int g, int b)? desired, CancellationToken ct)
        {
            if (!desired.HasValue) return;   // nothing configured: leave the backlight alone
            if (dial.Identifying) return;    // identify mode owns the backlight right now

            var color = desired.Value;
            DateTime now = DateTime.UtcNow;
            if (dial.LastSentBacklight.HasValue && dial.LastSentBacklight.Value == color && now - dial.LastBacklightSentUtc <= KeepAliveInterval)
            {
                return;
            }

            if (await server.UpdateDialBacklightAsync(dial.UID, color.r, color.g, color.b, ct: ct).ConfigureAwait(false))
            {
                dial.LastSentBacklight = color;
                dial.LastBacklightSentUtc = now;
            }
        }

        // ================================================================== IDialHost (actions from dial cards)

        void IDialHost.ApplySensor(DialViewModel vm, SensorOption option)
        {
            var dial = vm.Dial;
            dial.Sensor = option.Sensor;
            dial.SensorIdentifier = option.Identifier;
            dial.SensorName = option.Sensor?.Name ?? "";
            dial.ResetSentState();
            vm.SelectedSensorOption = option;

            ConfigManager.UpdateDialConfig(dial, true);
            Log.Information("Dial {Uid} now shows {Sensor}", dial.UID, option.Identifier);

            if (option.Sensor == null)
            {
                // Unassigned: park the needle at 0
                dial.Value = 0;
                _ = _server.UpdateDialValueAsync(dial.UID, 0);
            }
            vm.RefreshLive();
        }

        (float min, float max) IDialHost.ApplyScale(DialViewModel vm, string minText, string maxText)
        {
            var dial = vm.Dial;
            float min = ParseFloat(minText, dial.ScaleMin);
            float max = ParseFloat(maxText, dial.ScaleMax);
            if (min > max) (min, max) = (max, min);

            if (min != dial.ScaleMin || max != dial.ScaleMax)
            {
                dial.ScaleMin = min;
                dial.ScaleMax = max;
                dial.ResetSentState();
                ConfigManager.UpdateDialConfig(dial, true);
                Log.Information("Dial {Uid} scaling set to {Min}..{Max}", dial.UID, min, max);
            }
            return (min, max);
        }

        async Task IDialHost.RenameAsync(DialViewModel vm, string newName)
        {
            string oldName = vm.Name;
            vm.Name = newName;   // optimistic

            if (await _server.UpdateDialNameAsync(vm.Uid, newName))
            {
                vm.Dial.FriendlyName = newName;
            }
            else
            {
                vm.Name = oldName;
                MessageBox.Show(this, "Could not rename the dial. Is the VU1 server running?", "VU1 Demo App",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void IDialHost.OpenImageEditor(DialViewModel vm)
        {
            var editor = new DialImageEditorWindow(_server, ConfigManager, vm.Dial) { Owner = this };
            editor.ShowDialog();

            // Show whatever is on the dial now
            _ = RefreshDialImageAsync(vm);
        }

        void IDialHost.OpenBacklight(DialViewModel vm)
        {
            var window = new BacklightWindow(
                vm.Dial,
                _dialVms.Select(v => v.Dial).ToList(),
                ConfigManager,
                async (r, g, b) =>
                {
                    if (await _server.UpdateDialBacklightAsync(vm.Uid, r, g, b))
                    {
                        vm.Dial.LastSentBacklight = (r, g, b);
                        vm.Dial.LastBacklightSentUtc = DateTime.UtcNow;
                        vm.SetBacklight(r, g, b);
                        return true;
                    }
                    return false;
                })
            { Owner = this };
            window.ShowDialog();

            // Rules may have been copied to other dials as well
            foreach (var v in _dialVms)
            {
                v.Dial.ResetSentState();
                v.RefreshBacklightMode();
            }
        }

        async void IDialHost.Identify(DialViewModel vm)
        {
            var dial = vm.Dial;
            if (dial.Identifying) return;

            var server = _server;
            var (ir, ig, ib) = ConfigManager.GetIdentifyColor();
            int durationMs = ConfigManager.GetIdentifyDurationMs();

            // What to go back to afterwards
            var previous = dial.LastSentBacklight;
            var previousFromServer = dial.ServerBacklight;

            dial.Identifying = true;
            vm.IsIdentifying = true;
            vm.SetBacklight(ir, ig, ib);
            Log.Information("Identify dial {Uid} for {Ms} ms", dial.UID, durationMs);

            try
            {
                await server.UpdateDialBacklightAsync(dial.UID, ir, ig, ib);
                await Task.Delay(durationMs);

                // Restore: current rule / fixed colour if configured, otherwise the colour from before
                var restore = dial.DesiredBacklight(dial.Value) ?? previous;
                if (restore.HasValue)
                {
                    var (r, g, b) = restore.Value;
                    if (await server.UpdateDialBacklightAsync(dial.UID, r, g, b))
                    {
                        dial.LastSentBacklight = restore;
                        dial.LastBacklightSentUtc = DateTime.UtcNow;
                    }
                    vm.SetBacklight(r, g, b);
                }
                else if (previousFromServer.HasValue)
                {
                    // Values reported by the server are already percentages
                    var (r, g, b) = previousFromServer.Value;
                    await server.UpdateDialBacklightAsync(dial.UID, r, g, b, values_as_percent: true);
                    vm.SetBacklight(r, g, b);
                }
                else
                {
                    await server.UpdateDialBacklightAsync(dial.UID, 0, 0, 0);
                    vm.SetBacklight(0, 0, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Identify failed for dial {Uid}", dial.UID);
            }
            finally
            {
                dial.Identifying = false;
                vm.IsIdentifying = false;
            }
        }

        void IDialHost.ToggleMetrics()
        {
            _ui.MetricsExpanded = !_ui.MetricsExpanded;
            ConfigManager.SetMetricsExpanded(_ui.MetricsExpanded);
        }

        void IDialHost.SetFinish(DialViewModel vm, string finish) => SetFinish(new[] { vm }, finish);

        private void SetFinish(IEnumerable<DialViewModel> vms, string finish)
        {
            foreach (var vm in vms)
            {
                vm.Dial.Finish = ClassConfigurationManager.NormalizeFinish(finish);
                vm.RefreshFinish();
                ConfigManager.UpdateDialConfig(vm.Dial, false);
            }
            ConfigManager.SaveConfigFile();
        }

        void IDialHost.MoveDial(DialViewModel source, DialViewModel target)
        {
            int from = _dialVms.IndexOf(source);
            int to = _dialVms.IndexOf(target);
            if (from < 0 || to < 0 || from == to) return;

            _dialVms.Move(from, to);
            ConfigManager.SetDialOrder(_dialVms.Select(v => v.Uid));
            Log.Information("Dial {Uid} moved to position {Pos}", source.Uid, to + 1);
        }

        private static float ParseFloat(string text, float fallback) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;

        // ================================================================== status bar buttons

        private async void btnRefreshDials_click(object sender, RoutedEventArgs e)
        {
            await RefreshDialListAsync();
        }

        private void btnToggleDialUpdate_click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            btnToggleDialUpdate.Tag = FindResource(_paused ? "IconPlay" : "IconPause");
            btnToggleDialUpdate.ToolTip = _paused ? "Resume sending values to the dials" : "Pause sending values to the dials";
            if (!_paused)
            {
                foreach (var d in _dials) d.ResetSentState();
            }
            UpdateStatus();
        }

        private void btnAbout_click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        private async void btnSettings_click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow(ConfigManager, finish => SetFinish(_dialVms.ToList(), finish)) { Owner = this };
            if (settings.ShowDialog() == true)
            {
                await ReconnectAsync();
            }
        }

        /// <summary>Applies new server settings: replaces the server client and restarts the loop.</summary>
        private async Task ReconnectAsync()
        {
            Log.Information("Applying new server settings");
            await StopUpdateLoopAsync();

            var old = _server;
            _server = CreateServer();
            _hasConnectedOnce = false;
            old.Dispose();

            foreach (var d in _dials) d.ResetSentState();
            UpdateStatus();

            await RefreshDialListAsync();
            StartUpdateLoop();
        }

        // ================================================================== shutdown

        private void mainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            Log.Information("Main window closing");

            // Stop the update loop first so it doesn't overwrite the reset below
            _shutdownCts.Cancel();
            try { _updateLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancelled */ }

            // Reset each dial, but never hang the shutdown for more than a few seconds
            var server = _server;
            if (server.IsOnline)
            {
                var dials = _dials.ToList();
                var reset = Task.Run(async () =>
                {
                    foreach (ClassDialGUI dial in dials)
                    {
                        await server.UpdateDialValueAsync(dial.UID, 0).ConfigureAwait(false);
                        await server.UpdateDialBacklightAsync(dial.UID, 0, 0, 0).ConfigureAwait(false);
                    }
                });
                try { reset.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) { Log.Warning(ex, "Dial reset on exit failed"); }
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;   // otherwise a "ghost" icon stays in the tray
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            SensorManager.Dispose();
            server.Dispose();
        }
    }
}
