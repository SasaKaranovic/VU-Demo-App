using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibreHardwareMonitor.Hardware;
using VU1WPF.ImageEditor;

namespace VU1WPF
{
    /// <summary>Entry in the metric drop-down (grouped by sensor type).</summary>
    public sealed class SensorOption
    {
        public static readonly SensorOption None = new SensorOption(null);

        public SensorOption(ISensor? sensor)
        {
            Sensor = sensor;
            if (sensor == null)
            {
                Name = "Not assigned";
                Hardware = "";
                Category = " None";   // leading space: sorts first
                Identifier = "";
            }
            else
            {
                Name = sensor.Name;
                Hardware = sensor.Hardware?.Name ?? "";
                Category = sensor.SensorType.ToString();
                Identifier = sensor.Identifier.ToString();
            }
        }

        public ISensor? Sensor { get; }
        public string Name { get; }
        public string Hardware { get; }
        public string Category { get; }
        public string Identifier { get; }
        public string HardwareSuffix => string.IsNullOrEmpty(Hardware) ? "" : "  ·  " + Hardware;

        public override string ToString() => Sensor == null ? Name : $"{Name} - {Hardware}";
    }

    /// <summary>UI state shared by all dial cards (e.g. metric section expanded).</summary>
    public sealed class SharedUiState : INotifyPropertyChanged
    {
        private bool _metricsExpanded = true;

        public bool MetricsExpanded
        {
            get => _metricsExpanded;
            set
            {
                if (_metricsExpanded == value) return;
                _metricsExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetricsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Actions a dial card can request from the main window.</summary>
    internal interface IDialHost
    {
        void ApplySensor(DialViewModel vm, SensorOption option);
        /// <summary>Validates/saves scaling. Returns the normalised (min, max) to show.</summary>
        (float min, float max) ApplyScale(DialViewModel vm, string minText, string maxText);
        Task RenameAsync(DialViewModel vm, string newName);
        void OpenImageEditor(DialViewModel vm);
        void OpenBacklight(DialViewModel vm);
        void ToggleMetrics();
        void SetFinish(DialViewModel vm, string finish);
        /// <summary>Moves <paramref name="source"/> to the position of <paramref name="target"/>.</summary>
        void MoveDial(DialViewModel source, DialViewModel target);
        /// <summary>Briefly flashes the dial's backlight so it can be found on the desk.</summary>
        void Identify(DialViewModel vm);
    }

    /// <summary>
    /// UI state of one dial card: needle angle, live values, backlight colour and the
    /// e-ink screen image (tinted by the backlight).
    /// All members must be used on the UI thread.
    /// </summary>
    public sealed class DialViewModel : INotifyPropertyChanged
    {
        private const int W = DialDesign.DisplayWidth;
        private const int H = DialDesign.DisplayHeight;

        // E-ink look
        private static readonly Color EInkPaper = Color.FromRgb(0xD5, 0xD6, 0xD2);
        private static readonly Color EInkInk = Color.FromRgb(0x1C, 0x1D, 0x1F);
        private const double BacklightTintStrength = 0.38;

        private byte[] _displayGray;      // 200x144 grayscale of what's on the dial
        private Color? _backlight;        // display colour (normalised), null = off
        private string _name = "";
        private double _needleAngle = DialGeometry.AngleAtMin;
        private string _percentText = "";
        private string _rawText = "";
        private SensorOption _selectedSensorOption = SensorOption.None;
        private BitmapSource? _screenImage;
        private Brush _screenBrush = new SolidColorBrush(EInkPaper);

        internal DialViewModel(ClassDialGUI dial, IDialHost host, object sensorOptions, SharedUiState ui)
        {
            Dial = dial;
            Host = host;
            SensorOptions = sensorOptions;
            Ui = ui;
            _name = dial.FriendlyName;
            _displayGray = BlankDisplay();
            RebuildScreen();
            RefreshLive();
        }

        public ClassDialGUI Dial { get; }
        internal IDialHost Host { get; }

        /// <summary>Shared, grouped collection view of all <see cref="SensorOption"/>s.</summary>
        public object SensorOptions { get; }

        /// <summary>State shared by all cards (metric section expanded/collapsed).</summary>
        public SharedUiState Ui { get; }

        public string Uid => Dial.UID;
        public string ShortUid => Dial.UID.Length > 8 ? Dial.UID.Substring(0, 8) + "…" : Dial.UID;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public bool HasSensor => Dial.Sensor != null;

        public double NeedleAngle
        {
            get => _needleAngle;
            private set => Set(ref _needleAngle, value);
        }

        public string PercentText
        {
            get => _percentText;
            private set => Set(ref _percentText, value);
        }

        public string RawText
        {
            get => _rawText;
            private set
            {
                if (Set(ref _rawText, value)) OnPropertyChanged(nameof(StatusText));
            }
        }

        // ---- identify mode

        private static readonly Brush StatusMutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x8E, 0x94)));
        private static readonly Brush StatusHighlightBrush = Frozen(new SolidColorBrush(Colors.White));
        private bool _isIdentifying;

        public bool IsIdentifying
        {
            get => _isIdentifying;
            set
            {
                if (Set(ref _isIdentifying, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBrush));
                }
            }
        }

        /// <summary>Line under the dial name: live reading, or "Identifying…".</summary>
        public string StatusText => _isIdentifying ? "Identifying…" : _rawText;
        public Brush StatusBrush => _isIdentifying ? StatusHighlightBrush : StatusMutedBrush;

        public SensorOption SelectedSensorOption
        {
            get => _selectedSensorOption;
            set
            {
                if (Set(ref _selectedSensorOption, value ?? SensorOption.None))
                {
                    OnPropertyChanged(nameof(HasSensor));
                    RefreshLive();
                }
            }
        }

        // ---- physical finish (brushed aluminium, silver or black)

        public string Finish => Dial.Finish;
        public bool IsSilver => Dial.Finish == FinishTextures.Silver;
        public bool IsBlack => !IsSilver;
        public Brush HousingBrush => FinishTextures.Housing(Dial.Finish);
        public Brush HousingEdgeBrush => FinishTextures.Edge(Dial.Finish);
        public Brush HousingBevelBrush => FinishTextures.Bevel(Dial.Finish);
        public Brush GripBrush => FinishTextures.Grip(Dial.Finish);
        public Brush SilverSwatch => FinishTextures.Swatch(FinishTextures.Silver);
        public Brush BlackSwatch => FinishTextures.Swatch(FinishTextures.Black);

        /// <summary>Call after changing <see cref="ClassDialGUI.Finish"/>.</summary>
        public void RefreshFinish()
        {
            OnPropertyChanged(nameof(Finish));
            OnPropertyChanged(nameof(IsSilver));
            OnPropertyChanged(nameof(IsBlack));
            OnPropertyChanged(nameof(HousingBrush));
            OnPropertyChanged(nameof(HousingEdgeBrush));
            OnPropertyChanged(nameof(HousingBevelBrush));
            OnPropertyChanged(nameof(GripBrush));
        }

        /// <summary>"Rules", "Fixed" or "Off" - shown next to the Backlight button.</summary>
        public string BacklightModeText =>
            Dial.UsesRules ? "Rules"
            : Dial.FixedColor.HasValue && (Dial.FixedColor.Value.r > 0 || Dial.FixedColor.Value.g > 0 || Dial.FixedColor.Value.b > 0) ? "Fixed"
            : "Off";

        /// <summary>Call after the backlight configuration changed.</summary>
        public void RefreshBacklightMode()
        {
            OnPropertyChanged(nameof(BacklightModeText));
            if (!Dial.UsesRules && Dial.FixedColor.HasValue)
            {
                var (r, g, b) = Dial.FixedColor.Value;
                SetBacklight(r, g, b);
            }
        }

        // ---- screen / backlight

        public BitmapSource? ScreenImage
        {
            get => _screenImage;
            private set => Set(ref _screenImage, value);
        }

        /// <summary>Screen background (e-ink paper tinted by the backlight).</summary>
        public Brush ScreenBrush
        {
            get => _screenBrush;
            private set => Set(ref _screenBrush, value);
        }

        public bool IsBacklightOn => _backlight.HasValue;
        public Color GlowColor => _backlight ?? Colors.Black;
        public double GlowOpacity => _backlight.HasValue ? 0.85 : 0.0;
        public Brush LedBrush => _backlight.HasValue ? Frozen(new SolidColorBrush(_backlight.Value)) : Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x30)));
        public Brush SwatchBrush => _backlight.HasValue ? Frozen(new SolidColorBrush(_backlight.Value)) : Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x57, 0x5C)));

        /// <summary>
        /// Sets the backlight colour shown on screen from the app's 0..100 RGB values.
        /// The hue is normalised (brightest channel = 255) so dim colours stay recognisable.
        /// </summary>
        public void SetBacklight(int red, int green, int blue)
        {
            int max = Math.Max(red, Math.Max(green, blue));
            Color? newColor = null;
            if (max > 0)
            {
                newColor = Color.FromRgb(
                    (byte)Math.Clamp(red * 255 / max, 0, 255),
                    (byte)Math.Clamp(green * 255 / max, 0, 255),
                    (byte)Math.Clamp(blue * 255 / max, 0, 255));
            }

            if (Nullable.Equals(newColor, _backlight)) return;

            _backlight = newColor;
            OnPropertyChanged(nameof(IsBacklightOn));
            OnPropertyChanged(nameof(GlowColor));
            OnPropertyChanged(nameof(GlowOpacity));
            OnPropertyChanged(nameof(LedBrush));
            OnPropertyChanged(nameof(SwatchBrush));
            RebuildScreen();
        }

        /// <summary>Sets the image shown on the dial display (null/invalid = blank).</summary>
        public void SetDisplayImage(byte[]? imageBytes)
        {
            byte[] gray = BlankDisplay();
            if (imageBytes != null && imageBytes.Length > 0)
            {
                try
                {
                    var source = DialImageRenderer.DecodeImage(imageBytes);
                    gray = DialImageRenderer.RenderBaseGray(source, ImageFitMode.Stretch);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "Could not decode image of dial {Uid}", Dial.UID);
                }
            }

            _displayGray = gray;
            RebuildScreen();
        }

        /// <summary>Pulls the latest value/sensor reading from the model into the UI.</summary>
        public void RefreshLive()
        {
            ISensor? sensor = Dial.Sensor;
            if (sensor == null)
            {
                PercentText = "";
                RawText = "No metric selected";
                NeedleAngle = DialGeometry.AngleAtMin;
            }
            else
            {
                PercentText = $"{Dial.Value}%";
                RawText = FormatSensorValue(sensor);
                NeedleAngle = DialGeometry.PercentToAngle(Dial.Value);
            }

            if (Dial.LastSentBacklight.HasValue && !_isIdentifying)
            {
                var (r, g, b) = Dial.LastSentBacklight.Value;
                SetBacklight(r, g, b);
            }
        }

        public static string FormatSensorValue(ISensor sensor)
        {
            if (sensor.Value is not float v) return "—";

            string unit = sensor.SensorType switch
            {
                SensorType.Temperature => " °C",
                SensorType.Load => " %",
                SensorType.Control => " %",
                SensorType.Level => " %",
                SensorType.Clock => " MHz",
                SensorType.Frequency => " Hz",
                SensorType.Power => " W",
                SensorType.Voltage => " V",
                SensorType.Current => " A",
                SensorType.Fan => " RPM",
                SensorType.Flow => " L/h",
                SensorType.Data => " GB",
                SensorType.SmallData => " MB",
                SensorType.Throughput => " B/s",
                _ => "",
            };
            string format = sensor.SensorType == SensorType.Fan || sensor.SensorType == SensorType.Clock ? "0" : "0.0";
            return v.ToString(format, CultureInfo.CurrentCulture) + unit;
        }

        // ---- rendering of the e-ink screen

        private static byte[] BlankDisplay()
        {
            var gray = new byte[W * H];
            Array.Fill(gray, (byte)255);
            return gray;
        }

        private Color PaperColor()
        {
            if (!_backlight.HasValue) return EInkPaper;

            // "Multiply" blend of the paper colour with the backlight colour
            Color c = _backlight.Value;
            double a = BacklightTintStrength;
            byte Mix(byte paper, byte light) => (byte)Math.Clamp(paper * ((1 - a) + a * light / 255.0), 0, 255);
            return Color.FromRgb(Mix(EInkPaper.R, c.R), Mix(EInkPaper.G, c.G), Mix(EInkPaper.B, c.B));
        }

        private void RebuildScreen()
        {
            Color paper = PaperColor();
            Color ink = EInkInk;

            var pixels = new byte[W * H * 4];
            for (int i = 0, p = 0; i < _displayGray.Length; i++, p += 4)
            {
                double t = _displayGray[i] / 255.0;   // 0 = ink, 1 = paper
                pixels[p] = (byte)(ink.B + (paper.B - ink.B) * t);
                pixels[p + 1] = (byte)(ink.G + (paper.G - ink.G) * t);
                pixels[p + 2] = (byte)(ink.R + (paper.R - ink.R) * t);
                pixels[p + 3] = 255;
            }

            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, pixels, W * 4);
            bmp.Freeze();
            ScreenImage = bmp;
            ScreenBrush = Frozen(new SolidColorBrush(paper));
        }

        private static Brush Frozen(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        // ---- INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
