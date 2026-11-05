using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KR_VU1_ConfigurationManager;
using Serilog;

namespace VU1WPF
{
    /// <summary>
    /// Backlight settings for one dial:
    ///  - fixed colour (preset tiles or custom RGB), used while rules are off
    ///  - "rules by value": contiguous colour ranges over 0..100 % of the dial
    ///  - copy rules from another dial / apply them to all dials
    ///
    /// Ranges are stored as the existing threshold list: each rule is the upper bound (inclusive)
    /// of its range; the next range starts one above. The last range always ends at 100 %.
    /// </summary>
    public partial class BacklightWindow : Window
    {
        /// <summary>A preset / palette colour (RGB in 0..100 like the rest of the app).</summary>
        public sealed class PaletteColor
        {
            public PaletteColor(string name, int r, int g, int b)
            {
                Name = name; R = r; G = g; B = b;
                Color c = DisplayColor(r, g, b);
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                Brush = brush;
                GlowColor = c;
                GlowOpacity = (r | g | b) == 0 ? 0 : 0.6;
            }

            public string Name { get; }
            public int R { get; }
            public int G { get; }
            public int B { get; }
            public Brush Brush { get; }
            public Color GlowColor { get; }
            public double GlowOpacity { get; }
            public bool Matches(int r, int g, int b) => R == r && G == g && B == b;
            public override string ToString() => Name;
        }

        /// <summary>Tile in the fixed-colour grid.</summary>
        public sealed class TileItem
        {
            public TileItem(PaletteColor? color, bool selected)
            {
                Color = color;
                IsSelected = selected;
            }

            public PaletteColor? Color { get; }   // null = "Custom"
            public bool IsSelected { get; }
            public string Name => Color?.Name ?? "Custom";
            public Brush Brush => Color?.Brush ?? CustomBrush;
            public Color GlowColor => Color?.GlowColor ?? Colors.White;
            public double GlowOpacity => Color?.GlowOpacity ?? 0;
            public string Tooltip => Color == null ? "Pick any colour with RGB sliders" : $"Set the backlight to {Color.Name}";

            private static readonly Brush CustomBrush = CreateCustomBrush();
            private static Brush CreateCustomBrush()
            {
                var b = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(System.Windows.Media.Color.FromRgb(255, 60, 60), 0),
                        new GradientStop(System.Windows.Media.Color.FromRgb(255, 220, 40), 0.3),
                        new GradientStop(System.Windows.Media.Color.FromRgb(40, 220, 120), 0.55),
                        new GradientStop(System.Windows.Media.Color.FromRgb(40, 140, 255), 0.8),
                        new GradientStop(System.Windows.Media.Color.FromRgb(200, 60, 255), 1),
                    },
                };
                b.Freeze();
                return b;
            }
        }

        private static readonly List<PaletteColor> Palette = BacklightPresets.AvailablePresets
            .Select(p => new PaletteColor(p.Name, p.Red, p.Green, p.Blue))
            .ToList();

        /// <summary>Preset colours shared with other windows (Settings → identify colour).</summary>
        public static IReadOnlyList<PaletteColor> PaletteColors => Palette;

        // Colours used for new ranges, in this order
        private static readonly string[] NewRangeColors = { "White", "Green", "Yellow", "Orange", "Red", "Cyan", "Blue", "Magenta" };

        private readonly ClassDialGUI _dial;
        private readonly IReadOnlyList<ClassDialGUI> _allDials;
        private readonly ClassConfigurationManager _config;
        private readonly Func<int, int, int, Task<bool>> _sendColor;

        private List<ClassDialThreshold> _bands;
        private bool _loading = true;

        public BacklightWindow(ClassDialGUI dial, IReadOnlyList<ClassDialGUI> allDials, ClassConfigurationManager config,
                               Func<int, int, int, Task<bool>> sendColor)
        {
            _dial = dial;
            _allDials = allDials;
            _config = config;
            _sendColor = sendColor;
            _bands = Normalize(dial.Thresholds);

            InitializeComponent();
            DarkTitleBar.Apply(this);

            Title = $"Backlight · {dial.FriendlyName}";
            chkRules.IsChecked = dial.RulesEnabled;
            cbCopyFrom.ItemsSource = allDials.Where(d => !ReferenceEquals(d, dial)).ToList();
            cbCopyFrom.IsEnabled = allDials.Count > 1;

            if (dial.FixedColor.HasValue)
            {
                var (r, g, b) = dial.FixedColor.Value;
                SetCustomSliders(r, g, b);
            }

            RefreshTiles();
            RebuildRules();
            _loading = false;

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
        }

        /// <summary>Hue-normalised display colour for 0..100 RGB values (dark grey when off).</summary>
        public static Color DisplayColor(int r, int g, int b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            if (max <= 0) return Color.FromRgb(0x2A, 0x2C, 0x30);
            return Color.FromRgb((byte)(r * 255 / max), (byte)(g * 255 / max), (byte)(b * 255 / max));
        }

        private void SetStatus(string text) => txtStatus.Text = text;

        // ================================================================== fixed colour

        private void RefreshTiles()
        {
            var fixedColor = _dial.FixedColor;
            bool matchedPreset = false;
            var tiles = new List<TileItem>();
            foreach (var p in Palette)
            {
                bool sel = fixedColor.HasValue && p.Matches(fixedColor.Value.r, fixedColor.Value.g, fixedColor.Value.b);
                matchedPreset |= sel;
                tiles.Add(new TileItem(p, sel));
            }
            tiles.Add(new TileItem(null, fixedColor.HasValue && !matchedPreset));
            presetTiles.ItemsSource = tiles;
        }

        private async void PresetTile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TileItem tile) return;

            if (tile.Color == null)
            {
                customPanel.Visibility = customPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                return;
            }

            customPanel.Visibility = Visibility.Collapsed;
            SetCustomSliders(tile.Color.R, tile.Color.G, tile.Color.B);
            await SetFixedColorAsync(tile.Color.R, tile.Color.G, tile.Color.B, tile.Color.Name);
        }

        private void SetCustomSliders(int r, int g, int b)
        {
            sliderRed.Value = r;
            sliderGreen.Value = g;
            sliderBlue.Value = b;
            UpdateCustomSwatch();
        }

        private void CustomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateCustomSwatch();

        private void UpdateCustomSwatch()
        {
            if (customSwatch == null) return;   // during InitializeComponent
            customSwatch.Background = new SolidColorBrush(DisplayColor((int)sliderRed.Value, (int)sliderGreen.Value, (int)sliderBlue.Value));
        }

        private async void btnApplyCustom_Click(object sender, RoutedEventArgs e)
        {
            await SetFixedColorAsync((int)sliderRed.Value, (int)sliderGreen.Value, (int)sliderBlue.Value, "custom colour");
        }

        private async Task SetFixedColorAsync(int r, int g, int b, string label)
        {
            _dial.FixedColor = (r, g, b);
            Save();
            RefreshTiles();

            if (_dial.UsesRules)
            {
                SetStatus($"Fixed colour set to {label}. Rules are on, so it is used when you switch the rules off.");
                return;
            }

            bool ok = await _sendColor(r, g, b);
            SetStatus(ok ? $"Backlight set to {label}." : "Saved, but the VU1 server could not be reached.");
        }

        // ================================================================== rules: model helpers

        /// <summary>Sorted, de-duplicated copy; the last range always ends at 100 %.</summary>
        private static List<ClassDialThreshold> Normalize(IEnumerable<ClassDialThreshold> source)
        {
            var list = source
                .Where(t => t != null)
                .Select(t => new ClassDialThreshold
                {
                    Threshold = Math.Clamp(t.Threshold, 0, 100),
                    BacklightRed = t.BacklightRed,
                    BacklightGreen = t.BacklightGreen,
                    BacklightBlue = t.BacklightBlue,
                })
                .GroupBy(t => t.Threshold).Select(g => g.First())
                .OrderBy(t => t.Threshold)
                .ToList();

            if (list.Count > 0) list[^1].Threshold = 100;
            return list;
        }

        private static List<ClassDialThreshold> Clone(IEnumerable<ClassDialThreshold> source) =>
            source.Select(t => new ClassDialThreshold
            {
                Threshold = t.Threshold,
                BacklightRed = t.BacklightRed,
                BacklightGreen = t.BacklightGreen,
                BacklightBlue = t.BacklightBlue,
            }).ToList();

        private int RangeStart(int index) => index == 0 ? 0 : _bands[index - 1].Threshold + 1;

        private static PaletteColor PaletteByName(string name) =>
            Palette.FirstOrDefault(p => p.Name == name) ?? Palette[0];

        private PaletteColor NextNewRangeColor()
        {
            foreach (string name in NewRangeColors)
            {
                var p = PaletteByName(name);
                if (!_bands.Any(b => p.Matches(b.BacklightRed, b.BacklightGreen, b.BacklightBlue))) return p;
            }
            return PaletteByName("White");
        }

        /// <summary>Writes the edited ranges to the dial, saves the config and refreshes the UI.</summary>
        private void CommitRules()
        {
            CommitRulesData();
            RebuildRules();
        }

        private void CommitRulesData()
        {
            _bands = Normalize(_bands);
            _dial.Thresholds = Clone(_bands);   // new list: the update loop reads it from another thread
            Save();
        }

        private void Save()
        {
            _config.UpdateDialConfig(_dial, true);
            _dial.ResetSentState();
        }

        // ================================================================== rules: UI

        private void RebuildRules()
        {
            bool enabled = chkRules.IsChecked == true;
            rulesPanel.Opacity = enabled ? 1 : 0.45;

            // Colour bar
            rangeBar.Children.Clear();
            rangeBar.ColumnDefinitions.Clear();
            for (int i = 0; i < _bands.Count; i++)
            {
                int width = _bands[i].Threshold - RangeStart(i) + 1;
                rangeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, width), GridUnitType.Star) });
                var seg = new Border
                {
                    Background = new SolidColorBrush(DisplayColor(_bands[i].BacklightRed, _bands[i].BacklightGreen, _bands[i].BacklightBlue)),
                    CornerRadius = new CornerRadius(i == 0 ? 5 : 0, i == _bands.Count - 1 ? 5 : 0, i == _bands.Count - 1 ? 5 : 0, i == 0 ? 5 : 0),
                    Margin = new Thickness(0, 0, i == _bands.Count - 1 ? 0 : 2, 0),
                };
                Grid.SetColumn(seg, i);
                rangeBar.Children.Add(seg);
            }
            if (_bands.Count == 0)
            {
                rangeBar.Children.Add(new Border { Background = (Brush)FindResource("LineBrush"), CornerRadius = new CornerRadius(5) });
            }

            // Rows
            rulesRows.Children.Clear();
            for (int i = 0; i < _bands.Count; i++)
            {
                rulesRows.Children.Add(CreateRow(i));
            }
            txtNoRanges.Visibility = _bands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private UIElement CreateRow(int index)
        {
            var band = _bands[index];
            bool isLast = index == _bands.Count - 1;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });   // colour
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // range
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // delete

            // Colour picker (palette + this rule's colour if it isn't a preset)
            var items = new List<PaletteColor>(Palette);
            var current = items.FirstOrDefault(p => p.Matches(band.BacklightRed, band.BacklightGreen, band.BacklightBlue));
            if (current == null)
            {
                current = new PaletteColor($"Custom ({band.BacklightRed},{band.BacklightGreen},{band.BacklightBlue})",
                                           band.BacklightRed, band.BacklightGreen, band.BacklightBlue);
                items.Add(current);
            }

            var combo = new ComboBox
            {
                ItemsSource = items,
                SelectedItem = current,
                ItemTemplate = (DataTemplate)Resources["PaletteItemTemplate"],
                MinHeight = 30,
                Padding = new Thickness(8, 0, 28, 0),
                ToolTip = "Backlight colour for this range",
            };
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is PaletteColor p && !p.Matches(band.BacklightRed, band.BacklightGreen, band.BacklightBlue))
                {
                    band.BacklightRed = p.R;
                    band.BacklightGreen = p.G;
                    band.BacklightBlue = p.B;
                    CommitRules();
                }
            };
            Grid.SetColumn(combo, 0);
            grid.Children.Add(combo);

            // Range: "from X% to [Y] %"
            var range = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            range.Children.Add(Muted(index == 0 ? "from 0 %" : $"from {RangeStart(index)} %", 72));
            range.Children.Add(Muted("to", 22));
            if (isLast)
            {
                range.Children.Add(new TextBlock { Text = "100 %", VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                var box = new TextBox
                {
                    Text = band.Threshold.ToString(CultureInfo.InvariantCulture),
                    Width = 52,
                    MinHeight = 28,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    ToolTip = "Where this range ends (the next range starts one above)",
                };
                box.PreviewTextInput += (s, e) => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$") || box.Text.Length >= 3;
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { CommitRangeEnd(index, box); e.Handled = true; } };
                box.LostKeyboardFocus += (s, e) => CommitRangeEnd(index, box);
                range.Children.Add(box);
                range.Children.Add(new TextBlock { Text = "%", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            Grid.SetColumn(range, 1);
            grid.Children.Add(range);

            // Delete
            var delete = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Tag = FindResource("IconClose"),
                Width = 26,
                Height = 26,
                Padding = new Thickness(6),
                ToolTip = "Remove this range (the neighbouring range takes over)",
                IsEnabled = _bands.Count > 1,
            };
            delete.Click += (s, e) =>
            {
                _bands.RemoveAt(index);
                CommitRules();
            };
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);

            return new Border
            {
                Background = (Brush)FindResource("InputBrush"),
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = grid,
            };
        }

        private static TextBlock Muted(string text, double width) => new TextBlock
        {
            Text = text,
            Width = width,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private void CommitRangeEnd(int index, TextBox box)
        {
            if (index < 0 || index >= _bands.Count - 1) return;

            int min = RangeStart(index);
            int max = _bands[index + 1].Threshold - 1;
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                box.Text = _bands[index].Threshold.ToString(CultureInfo.InvariantCulture);
                return;
            }

            value = Math.Clamp(value, min, Math.Max(min, max));
            if (value == _bands[index].Threshold)
            {
                box.Text = value.ToString(CultureInfo.InvariantCulture);
                return;
            }

            _bands[index].Threshold = value;
            CommitRulesData();
            // Rebuild the rows after the focus change has finished
            Dispatcher.BeginInvoke(new Action(RebuildRules), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void btnAddRange_Click(object sender, RoutedEventArgs e)
        {
            var color = NextNewRangeColor();

            if (_bands.Count == 0)
            {
                _bands.Add(new ClassDialThreshold { Threshold = 100, BacklightRed = color.R, BacklightGreen = color.G, BacklightBlue = color.B });
            }
            else
            {
                int lastIndex = _bands.Count - 1;
                int start = RangeStart(lastIndex);
                if (100 - start < 1)
                {
                    SetStatus("The last range is too small to split. Make an earlier range end lower first.");
                    return;
                }

                int mid = start + (100 - start) / 2;
                _bands[lastIndex].Threshold = mid;
                _bands.Add(new ClassDialThreshold { Threshold = 100, BacklightRed = color.R, BacklightGreen = color.G, BacklightBlue = color.B });
            }

            if (chkRules.IsChecked != true) chkRules.IsChecked = true;   // adding rules implies using them
            CommitRules();
        }

        private void btnExample_Click(object sender, RoutedEventArgs e)
        {
            if (_bands.Count > 0 &&
                MessageBox.Show(this, "Replace the current ranges with the example?", "Backlight",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            ClassDialThreshold Band(int end, string name)
            {
                var p = PaletteByName(name);
                return new ClassDialThreshold { Threshold = end, BacklightRed = p.R, BacklightGreen = p.G, BacklightBlue = p.B };
            }

            _bands = new List<ClassDialThreshold> { Band(10, "White"), Band(50, "Green"), Band(80, "Yellow"), Band(100, "Red") };
            if (chkRules.IsChecked != true) chkRules.IsChecked = true;
            CommitRules();
            SetStatus("Example applied: white up to 10 %, green 11-50 %, yellow 51-80 %, red 81-100 %.");
        }

        private async void chkRules_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            _dial.RulesEnabled = chkRules.IsChecked == true;
            Save();
            RebuildRules();

            if (_dial.RulesEnabled)
            {
                SetStatus(_bands.Count == 0 ? "Rules are on. Add at least one range." : "Rules are on: the colour follows the dial value.");
            }
            else if (_dial.FixedColor.HasValue)
            {
                var (r, g, b) = _dial.FixedColor.Value;
                bool ok = await _sendColor(r, g, b);
                SetStatus(ok ? "Rules are off: using the fixed colour." : "Rules are off. The VU1 server could not be reached.");
            }
            else
            {
                SetStatus("Rules are off. Pick a fixed colour above.");
            }
        }

        // ================================================================== copy / apply to all

        private void cbCopyFrom_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbCopyFrom.SelectedItem is not ClassDialGUI source) return;

            _bands = Normalize(source.Thresholds);
            _dial.RulesEnabled = source.RulesEnabled;
            _loading = true;
            chkRules.IsChecked = source.RulesEnabled;
            _loading = false;
            CommitRules();
            SetStatus($"Copied {_bands.Count} range(s) from {source.FriendlyName}.");

            // Reset the drop-down so the same dial can be picked again
            Dispatcher.BeginInvoke(new Action(() => cbCopyFrom.SelectedItem = null), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void btnApplyAll_Click(object sender, RoutedEventArgs e)
        {
            var others = _allDials.Where(d => !ReferenceEquals(d, _dial)).ToList();
            if (others.Count == 0) return;

            if (MessageBox.Show(this, $"Copy these rules to {others.Count} other dial(s)? Their current rules will be replaced.",
                                "Backlight", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            bool enabled = chkRules.IsChecked == true;
            foreach (var d in others)
            {
                d.Thresholds = Clone(_bands);
                d.RulesEnabled = enabled;
                d.ResetSentState();
                _config.UpdateDialConfig(d, false);
            }
            _config.SaveConfigFile();
            Log.Information("Backlight rules of {Uid} applied to {Count} dial(s)", _dial.UID, others.Count);
            SetStatus($"Rules applied to {others.Count} other dial(s).");
        }

        private void btnDone_Click(object sender, RoutedEventArgs e)
        {
            // Commit a range end that is still being edited
            Keyboard.ClearFocus();
            Close();
        }
    }
}
