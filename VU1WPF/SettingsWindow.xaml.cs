using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using KR_VU1_ConfigurationManager;
using KR_VU1_Server;
using Serilog;

namespace VU1WPF
{
    /// <summary>Server URL, API key, update period and run-on-startup.</summary>
    public partial class SettingsWindow : Window
    {
        private readonly ClassConfigurationManager _config;
        private bool _revealed;

        private readonly Action<string>? _setAllFinish;

        public SettingsWindow(ClassConfigurationManager config, Action<string>? setAllFinish = null)
        {
            _config = config;
            _setAllFinish = setAllFinish;
            InitializeComponent();
            DarkTitleBar.Apply(this);

            txtServerUrl.Text = config.GetServerUrl();
            pwdApiKey.Password = config.GetMasterKey();
            txtPeriod.Text = config.GetDialUpdatePeriod().ToString("0.##", CultureInfo.InvariantCulture);
            chkRunOnStartup.IsChecked = StartupRegistration.IsEnabled();

            _identifyColor = config.GetIdentifyColor();
            txtIdentifyMs.Text = config.GetIdentifyDurationMs().ToString(CultureInfo.InvariantCulture);
            _updatingIdentify = true;
            sliderIdRed.Value = _identifyColor.r;
            sliderIdGreen.Value = _identifyColor.g;
            sliderIdBlue.Value = _identifyColor.b;
            _updatingIdentify = false;
            RefreshIdentifyColors();

            Loaded += (s, e) => txtServerUrl.Focus();
        }

        // ------------------------------------------------------------------ identify colour

        private (int r, int g, int b) _identifyColor = (100, 100, 100);
        private bool _updatingIdentify;

        private void RefreshIdentifyColors()
        {
            var palette = BacklightWindow.PaletteColors.Where(p => p.R + p.G + p.B > 0).ToList();   // "Off" makes no sense here
            var match = palette.FirstOrDefault(p => p.Matches(_identifyColor.r, _identifyColor.g, _identifyColor.b));
            var items = palette.Select(p => new BacklightWindow.TileItem(p, ReferenceEquals(p, match))).ToList();
            items.Add(new BacklightWindow.TileItem(null, match == null));
            identifyColors.ItemsSource = items;
            if (match == null) identifyCustom.Visibility = Visibility.Visible;
        }

        private void IdentifyColor_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BacklightWindow.TileItem tile) return;

            if (tile.Color == null)
            {
                identifyCustom.Visibility = identifyCustom.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                return;
            }

            identifyCustom.Visibility = Visibility.Collapsed;
            _identifyColor = (tile.Color.R, tile.Color.G, tile.Color.B);
            _updatingIdentify = true;
            sliderIdRed.Value = tile.Color.R;
            sliderIdGreen.Value = tile.Color.G;
            sliderIdBlue.Value = tile.Color.B;
            _updatingIdentify = false;
            RefreshIdentifyColors();
        }

        private void IdentifySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingIdentify || identifyColors == null) return;
            _identifyColor = ((int)sliderIdRed.Value, (int)sliderIdGreen.Value, (int)sliderIdBlue.Value);
            RefreshIdentifyColors();
        }

        private void btnAllSilver_Click(object sender, RoutedEventArgs e)
        {
            _setAllFinish?.Invoke(FinishTextures.Silver);
            ShowResult(true, "All dials set to silver.");
        }

        private void btnAllBlack_Click(object sender, RoutedEventArgs e)
        {
            _setAllFinish?.Invoke(FinishTextures.Black);
            ShowResult(true, "All dials set to black.");
        }

        private string ApiKey => _revealed ? txtApiKey.Text : pwdApiKey.Password;

        private void btnReveal_Click(object sender, RoutedEventArgs e)
        {
            if (_revealed)
            {
                pwdApiKey.Password = txtApiKey.Text;
                txtApiKey.Visibility = Visibility.Collapsed;
                pwdApiKey.Visibility = Visibility.Visible;
            }
            else
            {
                txtApiKey.Text = pwdApiKey.Password;
                pwdApiKey.Visibility = Visibility.Collapsed;
                txtApiKey.Visibility = Visibility.Visible;
            }
            _revealed = !_revealed;
        }

        private bool TryReadInputs(out string url, out float period)
        {
            period = 0;
            if (!VU1_Server.TryNormalizeServerUrl(txtServerUrl.Text, out url))
            {
                ShowResult(false, "Enter a valid server URL, e.g. http://localhost:5340");
                txtServerUrl.Focus();
                return false;
            }

            if (!float.TryParse(txtPeriod.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out period) ||
                !float.IsFinite(period) || period < 0.2f || period > 60f)
            {
                ShowResult(false, "Update period must be between 0.2 and 60 seconds.");
                txtPeriod.Focus();
                return false;
            }

            return true;
        }

        private async void btnTest_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadInputs(out string url, out _)) return;

            btnTest.IsEnabled = false;
            ShowResult(null, "Testing…");
            try
            {
                using var server = new VU1_Server(url, ApiKey, TimeSpan.FromSeconds(4));
                bool ok = await server.RefreshDialListAsync();
                if (ok)
                {
                    int n = server.GetDialList().Count;
                    ShowResult(true, $"Connection OK — {n} dial{(n == 1 ? "" : "s")} found");
                }
                else if (server.IsOnline)
                {
                    ShowResult(false, "Server reached, but the request was rejected. Check the API key.");
                }
                else
                {
                    ShowResult(false, "Could not reach the server at " + url);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Connection test failed");
                ShowResult(false, "Connection failed: " + ex.Message);
            }
            finally
            {
                btnTest.IsEnabled = true;
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadInputs(out string url, out float period)) return;

            if (!int.TryParse(txtIdentifyMs.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int identifyMs) ||
                identifyMs < ClassConfigurationManager.MinIdentifyMs || identifyMs > ClassConfigurationManager.MaxIdentifyMs)
            {
                ShowResult(false, $"Identify duration must be between {ClassConfigurationManager.MinIdentifyMs} and {ClassConfigurationManager.MaxIdentifyMs} ms.");
                txtIdentifyMs.Focus();
                return;
            }

            _config.SetIdentifySettings(_identifyColor, identifyMs, saveAfter: false);
            _config.UpdateServerSettings(url, ApiKey, period);

            bool wantStartup = chkRunOnStartup.IsChecked == true;
            if (wantStartup != StartupRegistration.IsEnabled())
            {
                try
                {
                    StartupRegistration.SetEnabled(wantStartup);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to change run-on-startup");
                    MessageBox.Show(this, "Could not change the start-up setting:\n" + ex.Message, "Settings",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            DialogResult = true;
        }

        /// <param name="ok">true = green, false = red, null = neutral</param>
        private void ShowResult(bool? ok, string text)
        {
            panelTestResult.Visibility = Visibility.Visible;
            string colorKey = ok == true ? "OkColor" : ok == false ? "DangerColor" : "MutedColor";
            var color = (Color)FindResource(colorKey);
            dotTest.Fill = new SolidColorBrush(color);
            txtTestResult.Text = text;
            txtTestResult.Foreground = ok == false ? new SolidColorBrush(color) : (Brush)FindResource("TextBrush");
        }
    }
}
