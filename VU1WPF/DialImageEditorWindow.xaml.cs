using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KR_VU1_ConfigurationManager;
using KR_VU1_Server;
using Microsoft.Win32;
using Serilog;
using VU1WPF.ImageEditor;

namespace VU1WPF
{
    /// <summary>
    /// Dial image editor:
    ///  - pick a template (bundled /image_pack, or user templates folder) or upload any image from disk
    ///  - image is fitted to 200x144, converted to grayscale and then to 1-bit (threshold or dithering)
    ///  - add/move/resize text and draw freehand on top
    ///  - save designs (re-open, edit and re-send later) and send the result to the dial
    /// </summary>
    public partial class DialImageEditorWindow : Window
    {
        /// <summary>Combo box entry (public so WPF data binding can read it).</summary>
        public sealed record Option(object Value, string Label);

        private const int W = DialDesign.DisplayWidth;
        private const int H = DialDesign.DisplayHeight;

        private static readonly Brush SelectedTextBackground = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x57, 0x22));

        private readonly VU1_Server _server;
        private readonly ClassConfigurationManager _config;
        private readonly ClassDialGUI _dial;
        private readonly DesignStore _store = new();
        private readonly DispatcherTimer _previewTimer;

        // Base image
        private BitmapSource? _baseSource;
        private byte[]? _baseBytes;
        private string? _baseExt;
        private string? _baseOrigin;
        private byte[]? _monoBase;
        private bool _baseDirty = true;
        private byte[]? _lastOutput;

        // Text items
        private readonly List<DesignTextItem> _texts = new();
        private readonly Dictionary<DesignTextItem, TextBlock> _textBlocks = new();
        private DesignTextItem? _selected;
        private bool _dragging;
        private Point _dragStartMouse;
        private double _dragStartX, _dragStartY;

        // Freehand undo
        private readonly Stack<(StrokeCollection added, StrokeCollection removed)> _undo = new();
        private bool _suppressUndo;

        // State flags
        private bool _initialized;
        private bool _updatingControls;
        private bool _loading;
        private bool _dirty;
        private string? _loadedDesignName;
        private List<string> _templatePaths = new();

        internal DialImageEditorWindow(VU1_Server server, ClassConfigurationManager config, ClassDialGUI dial)
        {
            _server = server;
            _config = config;
            _dial = dial;

            InitializeComponent();
            DarkTitleBar.Apply(this);

            // Default size shows everything without scroll bars; only shrink on small screens
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
            if (Height > MaxHeight) Height = MaxHeight;
            if (MinWidth > MaxWidth) MinWidth = MaxWidth;

            _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); RebuildPreview(); };

            txtHeader.Text = $"Dial: {dial.FriendlyName}   ({dial.UID})";
            Title = $"Dial Image Editor - {dial.FriendlyName}";

            cbFit.DisplayMemberPath = nameof(Option.Label);
            cbFit.SelectedValuePath = nameof(Option.Value);
            cbFit.ItemsSource = new[]
            {
                new Option(ImageFitMode.Fit, "Fit (keep whole image)"),
                new Option(ImageFitMode.Fill, "Fill (crop to fill)"),
                new Option(ImageFitMode.Stretch, "Stretch"),
                new Option(ImageFitMode.Center, "Center (no scaling)"),
            };
            cbFit.SelectedValue = ImageFitMode.Fit;

            cbMode.DisplayMemberPath = nameof(Option.Label);
            cbMode.SelectedValuePath = nameof(Option.Value);
            cbMode.ItemsSource = new[]
            {
                new Option(MonochromeMode.Threshold, "Threshold (text / line art)"),
                new Option(MonochromeMode.FloydSteinberg, "Dither: Floyd-Steinberg (photos)"),
                new Option(MonochromeMode.Atkinson, "Dither: Atkinson (high contrast)"),
            };
            cbMode.SelectedValue = MonochromeMode.Threshold;

            cbFont.ItemsSource = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Distinct()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            cbFont.SelectedItem = "Segoe UI";

            SetStrokes(new StrokeCollection());

            _initialized = true;
            ApplyTool();
            ApplyBrush();

            RefreshTemplates(force: true);
            RefreshDesignList();

            Activated += (s, e) => RefreshTemplates(force: false);

            // Re-open the design last sent to this dial, if any
            string lastDesign = _config.GetDialDesign(dial.UID);
            if (!string.IsNullOrEmpty(lastDesign) && _store.Exists(lastDesign))
            {
                LoadDesign(lastDesign);
            }
            else
            {
                txtDesignName.Text = "";
                SchedulePreview();
            }
        }

        // ================================================================== preview pipeline

        private void SchedulePreview()
        {
            if (!_initialized) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void MarkDirty()
        {
            if (!_loading) _dirty = true;
        }

        private ImageFitMode CurrentFit => cbFit.SelectedValue is ImageFitMode f ? f : ImageFitMode.Fit;
        private MonochromeMode CurrentMode => cbMode.SelectedValue is MonochromeMode m ? m : MonochromeMode.Threshold;
        private int CurrentThreshold => (int)Math.Round(sliderThreshold.Value);

        /// <summary>Re-renders the base (when needed) and the final 1-bit output.</summary>
        private void RebuildPreview()
        {
            try
            {
                if (_baseDirty || _monoBase == null)
                {
                    byte[] gray = DialImageRenderer.RenderBaseGray(_baseSource, CurrentFit);
                    _monoBase = MonochromeConverter.ToMonochrome(gray, W, H, CurrentMode, CurrentThreshold, chkInvert.IsChecked == true);
                    imgBase.Source = DialImageRenderer.GrayToBitmap(_monoBase);
                    _baseDirty = false;
                }

                _lastOutput = DialImageRenderer.RenderFinal(_monoBase, inkCanvas.Strokes, _texts);
                imgPreview.Source = DialImageRenderer.GrayToBitmap(_lastOutput);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to render dial image preview");
                SetStatus("Could not render image: " + ex.Message, isError: true);
            }
        }

        private byte[] GetOutputPng()
        {
            _previewTimer.Stop();
            RebuildPreview();
            if (_lastOutput == null) throw new InvalidOperationException("Nothing to render.");
            return DialImageRenderer.EncodePng(_lastOutput);
        }

        private void Processing_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _updatingControls) return;
            _baseDirty = true;
            MarkDirty();
            SchedulePreview();
        }

        // ================================================================== base image

        /// <summary>Sets the background image. Pass null bytes for a blank (white) background.</summary>
        private bool SetBase(byte[]? bytes, string? extension, string? origin, bool autoSelectMode)
        {
            BitmapSource? source = null;
            if (bytes != null)
            {
                try
                {
                    source = DialImageRenderer.DecodeImage(bytes);
                    // Very large photos: decode smaller to keep memory/CPU low (output is only 200x144)
                    if (source.PixelWidth > 1600)
                    {
                        source = DialImageRenderer.DecodeImage(bytes, 800);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not decode image {Origin}", origin);
                    MessageBox.Show(this, "This file could not be opened as an image.\n\n" + ex.Message,
                                    "Dial Image Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            _baseSource = source;
            _baseBytes = bytes;
            _baseExt = extension;
            _baseOrigin = origin;
            _baseDirty = true;

            if (autoSelectMode && source != null)
            {
                // Photos (many gray levels) look much better dithered; line art/templates with a threshold.
                byte[] gray = DialImageRenderer.RenderBaseGray(source, CurrentFit);
                int levels = gray.Distinct().Count();
                _updatingControls = true;
                cbMode.SelectedValue = levels > 48 ? MonochromeMode.FloydSteinberg : MonochromeMode.Threshold;
                _updatingControls = false;
            }

            MarkDirty();
            SchedulePreview();
            return true;
        }

        private void RefreshTemplates(bool force)
        {
            // Cheap file listing first; thumbnails are only decoded when the folder content changed.
            var paths = TemplateLibrary.ListPaths().Select(p => p.path).ToList();
            if (!force && paths.SequenceEqual(_templatePaths)) return;

            var templates = TemplateLibrary.LoadAll();
            _templatePaths = paths;
            lbTemplates.SelectionChanged -= lbTemplates_SelectionChanged;
            lbTemplates.ItemsSource = templates;
            lbTemplates.SelectedItem = null;
            lbTemplates.SelectionChanged += lbTemplates_SelectionChanged;
        }

        private void lbTemplates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lbTemplates.SelectedItem is not TemplateInfo t) return;

            try
            {
                byte[] bytes = File.ReadAllBytes(t.Path);
                if (SetBase(bytes, Path.GetExtension(t.Path), t.Path, autoSelectMode: true))
                {
                    SetStatus($"Template \"{t.Name}\" selected.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read template {Path}", t.Path);
                SetStatus("Could not read template: " + ex.Message, isError: true);
            }
        }

        private void btnOpenImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose an image for the dial",
                Filter = TemplateLibrary.FileDialogFilter,
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var info = new FileInfo(dlg.FileName);
                if (info.Length > 50 * 1024 * 1024)
                {
                    MessageBox.Show(this, "That file is too large (max 50 MB).", "Dial Image Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                byte[] bytes = File.ReadAllBytes(dlg.FileName);
                if (SetBase(bytes, Path.GetExtension(dlg.FileName), dlg.FileName, autoSelectMode: true))
                {
                    lbTemplates.SelectedItem = null;
                    SetStatus($"Loaded {Path.GetFileName(dlg.FileName)}. Adjust fit, 1-bit mode and threshold as needed.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read image {Path}", dlg.FileName);
                SetStatus("Could not read file: " + ex.Message, isError: true);
            }
        }

        private void btnBlank_Click(object sender, RoutedEventArgs e)
        {
            lbTemplates.SelectedItem = null;
            SetBase(null, null, null, autoSelectMode: false);
            SetStatus("Blank background.");
        }

        private void btnOpenTemplatesFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.UserTemplatesDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.UserTemplatesDir,
                    UseShellExecute = true,
                });
                SetStatus("Add images (200x144 px recommended) to this folder - they appear in the list automatically.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open templates folder");
                SetStatus("Templates folder: " + AppPaths.UserTemplatesDir);
            }
        }

        // ================================================================== tools / freehand

        private void Tool_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            ApplyTool();
        }

        private void Brush_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            ApplyBrush();
        }

        private bool IsTextTool => rbToolText.IsChecked == true;

        private void ApplyTool()
        {
            if (rbToolDraw.IsChecked == true)
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                inkCanvas.IsHitTestVisible = true;
                textLayer.IsHitTestVisible = false;
                txtHint.Text = "Draw tool: draw with the mouse/pen. Use white ink to hide parts of a template. Ctrl+Z to undo.";
                SelectText(null);
            }
            else if (rbToolErase.IsChecked == true)
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
                inkCanvas.IsHitTestVisible = true;
                textLayer.IsHitTestVisible = false;
                txtHint.Text = "Erase tool: erases your freehand strokes (not the background image).";
                SelectText(null);
            }
            else
            {
                inkCanvas.EditingMode = InkCanvasEditingMode.None;
                inkCanvas.IsHitTestVisible = false;
                textLayer.IsHitTestVisible = true;
                txtHint.Text = "Text tool: double-click to add text, drag to move, arrow keys to nudge (Shift = 5px), Delete to remove.";
            }
            ApplyBrush();
        }

        private void ApplyBrush()
        {
            double size = Math.Max(1, sliderBrush.Value);
            inkCanvas.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = rbInkWhite.IsChecked == true ? Colors.White : Colors.Black,
                Width = size,
                Height = size,
                FitToCurve = false,
                IgnorePressure = true,
                StylusTip = StylusTip.Ellipse,
            };
            inkCanvas.EraserShape = new EllipseStylusShape(size * 2, size * 2);
        }

        private void SetStrokes(StrokeCollection strokes)
        {
            inkCanvas.Strokes.StrokesChanged -= Strokes_StrokesChanged;
            inkCanvas.Strokes = strokes;
            inkCanvas.Strokes.StrokesChanged += Strokes_StrokesChanged;
            _undo.Clear();
        }

        private void Strokes_StrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
        {
            if (!_suppressUndo)
            {
                _undo.Push((new StrokeCollection(e.Added), new StrokeCollection(e.Removed)));
            }
            MarkDirty();
            SchedulePreview();
        }

        private void btnUndo_Click(object sender, RoutedEventArgs e) => Undo();

        private void Undo()
        {
            if (_undo.Count == 0) return;

            var (added, removed) = _undo.Pop();
            _suppressUndo = true;
            try
            {
                foreach (Stroke s in added)
                {
                    if (inkCanvas.Strokes.Contains(s)) inkCanvas.Strokes.Remove(s);
                }
                foreach (Stroke s in removed)
                {
                    if (!inkCanvas.Strokes.Contains(s)) inkCanvas.Strokes.Add(s);
                }
            }
            finally
            {
                _suppressUndo = false;
            }
        }

        private void btnClearDrawing_Click(object sender, RoutedEventArgs e)
        {
            if (inkCanvas.Strokes.Count == 0) return;
            inkCanvas.Strokes.Clear();   // undoable
        }

        // ================================================================== text items

        private void AddTextItem(DesignTextItem item, bool select)
        {
            _texts.Add(item);

            var tb = new TextBlock
            {
                Cursor = Cursors.SizeAll,
                Background = Brushes.Transparent,
                Tag = item,
            };
            tb.MouseLeftButtonDown += TextBlock_MouseLeftButtonDown;
            tb.MouseMove += TextBlock_MouseMove;
            tb.MouseLeftButtonUp += TextBlock_MouseLeftButtonUp;

            _textBlocks[item] = tb;
            textLayer.Children.Add(tb);
            UpdateTextBlock(item);

            item.PropertyChanged += TextItem_PropertyChanged;

            if (select) SelectText(item);
            MarkDirty();
            SchedulePreview();
        }

        private void RemoveTextItem(DesignTextItem item)
        {
            item.PropertyChanged -= TextItem_PropertyChanged;
            if (_textBlocks.TryGetValue(item, out var tb))
            {
                textLayer.Children.Remove(tb);
                _textBlocks.Remove(item);
            }
            _texts.Remove(item);
            if (_selected == item) SelectText(null);
            MarkDirty();
            SchedulePreview();
        }

        private void ClearTextItems()
        {
            foreach (var item in _texts.ToList())
            {
                item.PropertyChanged -= TextItem_PropertyChanged;
            }
            _texts.Clear();
            _textBlocks.Clear();
            textLayer.Children.Clear();
            SelectText(null);
        }

        private void TextItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is DesignTextItem item)
            {
                UpdateTextBlock(item);
                MarkDirty();
                SchedulePreview();
            }
        }

        private void UpdateTextBlock(DesignTextItem item)
        {
            if (!_textBlocks.TryGetValue(item, out var tb)) return;

            tb.Text = item.Text;
            tb.FontFamily = new FontFamily(item.FontFamily);
            tb.FontSize = item.FontSize;
            tb.FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal;
            tb.Foreground = item.White ? Brushes.White : Brushes.Black;
            tb.Background = item == _selected ? SelectedTextBackground : Brushes.Transparent;
            Canvas.SetLeft(tb, Math.Round(item.X));
            Canvas.SetTop(tb, Math.Round(item.Y));
        }

        private void SelectText(DesignTextItem? item)
        {
            var previous = _selected;
            _selected = item;
            if (previous != null) UpdateTextBlock(previous);
            if (item != null) UpdateTextBlock(item);

            panelText.IsEnabled = item != null;

            if (item == null) return;

            _updatingControls = true;
            try
            {
                txtText.Text = item.Text;
                if (cbFont.ItemsSource is List<string> fonts && !fonts.Contains(item.FontFamily))
                {
                    fonts.Add(item.FontFamily);
                    cbFont.Items.Refresh();
                }
                cbFont.SelectedItem = item.FontFamily;
                sliderFontSize.Value = item.FontSize;
                chkBold.IsChecked = item.Bold;
                chkWhiteText.IsChecked = item.White;
            }
            finally
            {
                _updatingControls = false;
            }
        }

        /// <summary>New text uses the style currently shown in the text panel.</summary>
        private DesignTextItem NewTextFromControls(string text) => new DesignTextItem
        {
            Text = text,
            FontFamily = cbFont.SelectedItem as string ?? "Segoe UI",
            FontSize = sliderFontSize.Value,
            Bold = chkBold.IsChecked == true,
            White = chkWhiteText.IsChecked == true,
        };

        private void txtText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized || _updatingControls || _selected == null) return;
            _selected.Text = txtText.Text;
        }

        private void TextStyle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _updatingControls || _selected == null) return;

            _selected.FontFamily = cbFont.SelectedItem as string ?? _selected.FontFamily;
            _selected.FontSize = sliderFontSize.Value;
            _selected.Bold = chkBold.IsChecked == true;
            _selected.White = chkWhiteText.IsChecked == true;
        }

        private void btnAddText_Click(object sender, RoutedEventArgs e)
        {
            rbToolText.IsChecked = true;

            var item = NewTextFromControls("Text");
            Size size = DialImageRenderer.MeasureText(item);
            item.X = Math.Round((W - size.Width) / 2);
            item.Y = Math.Round((H - size.Height) / 2);
            AddTextItem(item, select: true);

            txtText.Focus();
            txtText.SelectAll();
        }

        private void btnCenterText_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            Size size = DialImageRenderer.MeasureText(_selected);
            _selected.X = Math.Round((W - size.Width) / 2);
        }

        private void btnDuplicateText_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var copy = _selected.Clone();
            copy.X += 6;
            copy.Y += 6;
            AddTextItem(copy, select: true);
        }

        private void btnDeleteText_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null) RemoveTextItem(_selected);
        }

        private void textLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Clicks on TextBlocks are handled (and marked Handled) by the TextBlock itself.
            if (!IsTextTool) return;

            if (e.ClickCount == 2)
            {
                Point p = e.GetPosition(textLayer);
                var item = NewTextFromControls("Text");
                item.X = Math.Round(p.X);
                item.Y = Math.Round(p.Y);
                AddTextItem(item, select: true);
                txtText.Focus();
                txtText.SelectAll();
            }
            else
            {
                SelectText(null);
                Keyboard.Focus(this);
            }
            e.Handled = true;
        }

        private void TextBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsTextTool || sender is not TextBlock tb || tb.Tag is not DesignTextItem item) return;

            SelectText(item);

            if (e.ClickCount == 2)
            {
                txtText.Focus();
                txtText.SelectAll();
            }
            else
            {
                _dragging = true;
                _dragStartMouse = e.GetPosition(textLayer);
                _dragStartX = item.X;
                _dragStartY = item.Y;
                tb.CaptureMouse();
                Keyboard.Focus(this);
            }
            e.Handled = true;
        }

        private void TextBlock_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _selected == null || e.LeftButton != MouseButtonState.Pressed) return;

            Point p = e.GetPosition(textLayer);
            _selected.X = Math.Round(_dragStartX + (p.X - _dragStartMouse.X));
            _selected.Y = Math.Round(_dragStartY + (p.Y - _dragStartMouse.Y));
        }

        private void TextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool typingInControl = Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or Slider or ListBoxItem;

            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !typingInControl)
            {
                Undo();
                e.Handled = true;
                return;
            }

            if (_selected == null || !IsTextTool || typingInControl) return;

            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
            switch (e.Key)
            {
                case Key.Left: _selected.X -= step; e.Handled = true; break;
                case Key.Right: _selected.X += step; e.Handled = true; break;
                case Key.Up: _selected.Y -= step; e.Handled = true; break;
                case Key.Down: _selected.Y += step; e.Handled = true; break;
                case Key.Delete: RemoveTextItem(_selected); e.Handled = true; break;
            }
        }

        // ================================================================== designs: save / load

        private DialDesign BuildDesign(string name)
        {
            string? isf = null;
            if (inkCanvas.Strokes.Count > 0)
            {
                using var ms = new MemoryStream();
                inkCanvas.Strokes.Save(ms);
                isf = Convert.ToBase64String(ms.ToArray());
            }

            return new DialDesign
            {
                Name = name,
                BaseImageOrigin = _baseOrigin,
                FitMode = CurrentFit,
                Mode = CurrentMode,
                Threshold = CurrentThreshold,
                Invert = chkInvert.IsChecked == true,
                Texts = _texts.Select(t => t.Clone()).ToList(),
                StrokesIsf = isf,
            };
        }

        /// <summary>Saves the current design under <paramref name="name"/>. Returns the PNG output, or null if cancelled/failed.</summary>
        private byte[]? SaveDesign(string name)
        {
            name = name.Trim();
            if (DesignStore.ToSafeName(name).Length == 0)
            {
                SetStatus("Enter a design name first.", isError: true);
                txtDesignName.Focus();
                return null;
            }

            bool isSameDesign = _loadedDesignName != null &&
                                DesignStore.ToSafeName(_loadedDesignName).Equals(DesignStore.ToSafeName(name), StringComparison.OrdinalIgnoreCase);
            if (!isSameDesign && _store.Exists(name))
            {
                var answer = MessageBox.Show(this, $"A design named \"{name}\" already exists. Overwrite it?",
                                             "Dial Image Editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return null;
            }

            try
            {
                byte[] png = GetOutputPng();
                _store.Save(BuildDesign(name), _baseBytes, _baseExt, png);
                _loadedDesignName = name;
                txtDesignName.Text = name;
                _dirty = false;
                RefreshDesignList();
                return png;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save design {Name}", name);
                SetStatus("Could not save design: " + ex.Message, isError: true);
                return null;
            }
        }

        private void LoadDesign(string name)
        {
            DialDesign design;
            byte[]? baseBytes;
            try
            {
                (design, baseBytes) = _store.Load(name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load design {Name}", name);
                SetStatus("Could not open design: " + ex.Message, isError: true);
                return;
            }

            _loading = true;
            try
            {
                _updatingControls = true;
                cbFit.SelectedValue = design.FitMode;
                cbMode.SelectedValue = design.Mode;
                sliderThreshold.Value = Math.Clamp(design.Threshold, 1, 255);
                chkInvert.IsChecked = design.Invert;
                _updatingControls = false;

                lbTemplates.SelectionChanged -= lbTemplates_SelectionChanged;
                lbTemplates.SelectedItem = null;
                lbTemplates.SelectionChanged += lbTemplates_SelectionChanged;

                SetBase(baseBytes, design.BaseImageFile != null ? Path.GetExtension(design.BaseImageFile) : null,
                        design.BaseImageOrigin, autoSelectMode: false);

                StrokeCollection strokes = new();
                if (!string.IsNullOrEmpty(design.StrokesIsf))
                {
                    try
                    {
                        using var ms = new MemoryStream(Convert.FromBase64String(design.StrokesIsf));
                        strokes = new StrokeCollection(ms);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not read strokes of design {Name}", name);
                    }
                }
                SetStrokes(strokes);

                ClearTextItems();
                foreach (var t in design.Texts)
                {
                    AddTextItem(t, select: false);
                }

                _loadedDesignName = design.Name;
                txtDesignName.Text = design.Name;
                _baseDirty = true;
                SchedulePreview();
                SetStatus($"Opened design \"{design.Name}\".");
            }
            finally
            {
                _updatingControls = false;
                _loading = false;
                _dirty = false;
            }
        }

        private void RefreshDesignList()
        {
            var designs = _store.List();
            foreach (var d in designs)
            {
                if (d.PreviewPath != null)
                {
                    d.Thumbnail = DialImageRenderer.TryLoadThumbnail(d.PreviewPath, 0);
                }
            }
            lbDesigns.ItemsSource = designs;
        }

        private bool ConfirmDiscardChanges()
        {
            if (!_dirty) return true;
            return MessageBox.Show(this, "Discard unsaved changes to the current design?", "Dial Image Editor",
                                   MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void btnLoadDesign_Click(object sender, RoutedEventArgs e)
        {
            if (lbDesigns.SelectedItem is not DesignInfo info)
            {
                SetStatus("Select a saved design first.");
                return;
            }
            if (!ConfirmDiscardChanges()) return;
            LoadDesign(info.Name);
        }

        private void lbDesigns_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            btnLoadDesign_Click(sender, e);
        }

        private void btnDeleteDesign_Click(object sender, RoutedEventArgs e)
        {
            if (lbDesigns.SelectedItem is not DesignInfo info) return;

            if (MessageBox.Show(this, $"Delete design \"{info.Name}\"?", "Dial Image Editor",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _store.Delete(info.Name);
                if (_loadedDesignName != null && DesignStore.ToSafeName(_loadedDesignName) == DesignStore.ToSafeName(info.Name))
                {
                    _loadedDesignName = null;
                    _dirty = true;
                }
                RefreshDesignList();
                SetStatus($"Deleted \"{info.Name}\".");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete design {Name}", info.Name);
                SetStatus("Could not delete: " + ex.Message, isError: true);
            }
        }

        private void btnSaveDesign_Click(object sender, RoutedEventArgs e)
        {
            if (SaveDesign(txtDesignName.Text) != null)
            {
                SetStatus($"Design \"{txtDesignName.Text}\" saved.");
            }
        }

        private void btnExportPng_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export dial image",
                Filter = "PNG image|*.png",
                FileName = DesignStore.ToSafeName(string.IsNullOrWhiteSpace(txtDesignName.Text) ? _dial.FriendlyName : txtDesignName.Text) + ".png",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllBytes(dlg.FileName, GetOutputPng());
                SetStatus("Exported " + Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Export failed");
                SetStatus("Export failed: " + ex.Message, isError: true);
            }
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            string name = txtDesignName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = $"{_dial.FriendlyName} {DateTime.Now:yyyy-MM-dd HH.mm}";
            }

            // Every design sent to a dial is saved, so it can be re-opened, edited and re-sent later.
            byte[]? png = SaveDesign(name);
            if (png == null) return;

            btnSend.IsEnabled = false;
            SetStatus("Sending to dial…");
            try
            {
                bool ok = await _server.UploadDialImageAsync(_dial.UID, png, DesignStore.ToSafeName(name) + ".png");
                if (ok)
                {
                    _config.SetDialDesign(_dial.UID, name);
                    SetStatus($"Sent \"{name}\" to {_dial.FriendlyName}. (E-ink update takes a few seconds.)");
                }
                else
                {
                    SetStatus("Could not send the image. Is the VU1 server running? The design was saved.", isError: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Sending image to dial {Uid} failed", _dial.UID);
                SetStatus("Sending failed: " + ex.Message, isError: true);
            }
            finally
            {
                btnSend.IsEnabled = true;
            }
        }

        // ================================================================== misc

        private void SetStatus(string text, bool isError = false)
        {
            txtStatus.Text = text;
            txtStatus.Foreground = isError ? Brushes.Firebrick : (Brush)FindResource("TextBrush");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmDiscardChanges())
            {
                e.Cancel = true;
                return;
            }
            _previewTimer.Stop();
        }
    }
}
