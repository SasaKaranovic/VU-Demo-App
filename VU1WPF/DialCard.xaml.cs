using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VU1WPF
{
    /// <summary>
    /// One dial: a drawing of the VU1 dial (screen image, needle, backlight) plus its
    /// metric and scaling controls. DataContext is a <see cref="DialViewModel"/>.
    /// </summary>
    public partial class DialCard : UserControl
    {
        private static readonly Regex NumericInput = new(@"^[0-9.\-]+$");
        private DialViewModel? _vm;

        public DialCard()
        {
            InitializeComponent();

            // Needle geometry comes from compile-time constants (see DialGeometry.cs)
            double px = DialGeometry.PivotX, py = DialGeometry.PivotY;
            NeedleRotate.CenterX = px;
            NeedleRotate.CenterY = py;

            NeedleBase.X1 = px; NeedleBase.Y1 = py - DialGeometry.NeedleInnerRadius;
            NeedleBase.X2 = px; NeedleBase.Y2 = py - DialGeometry.NeedleSplitRadius;
            NeedleTip.X1 = px; NeedleTip.Y1 = py - DialGeometry.NeedleSplitRadius;
            NeedleTip.X2 = px; NeedleTip.Y2 = py - DialGeometry.NeedleTipRadius;
            NeedleBase.StrokeThickness = DialGeometry.NeedleThickness;
            NeedleTip.StrokeThickness = DialGeometry.NeedleThickness;
            NeedleRotate.Angle = DialGeometry.AngleAtMin;

            DataContextChanged += OnDataContextChanged;
            Loaded += (s, e) => Attach();
            Unloaded += (s, e) => Detach();   // the shared UI state outlives the card
        }

        private bool _attached;

        private void Attach()
        {
            if (_attached || _vm == null) return;
            _vm.PropertyChanged += Vm_PropertyChanged;
            _vm.Ui.PropertyChanged += Ui_PropertyChanged;
            _attached = true;
        }

        private void Detach()
        {
            if (!_attached || _vm == null) return;
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm.Ui.PropertyChanged -= Ui_PropertyChanged;
            _attached = false;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Detach();

            _vm = DataContext as DialViewModel;
            if (_vm == null) return;

            Attach();
            ApplyMetricsExpanded(_vm.Ui.MetricsExpanded, animate: false);
            ApplyIdentify(_vm.IsIdentifying);

            // Initial state without animation
            NeedleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            NeedleRotate.Angle = _vm.NeedleAngle;
            // Re-apply the selection once the ItemsSource binding is in place
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_vm != null && !ReferenceEquals(cbSensor.SelectedItem, _vm.SelectedSensorOption))
                {
                    cbSensor.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, _vm.SelectedSensorOption);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            ShowScale(_vm.Dial.ScaleMin, _vm.Dial.ScaleMax);
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_vm == null) return;

            switch (e.PropertyName)
            {
                case nameof(DialViewModel.NeedleAngle):
                    AnimateNeedle(_vm.NeedleAngle);
                    break;
                case nameof(DialViewModel.IsIdentifying):
                    ApplyIdentify(_vm.IsIdentifying);
                    break;
                case nameof(DialViewModel.SelectedSensorOption):
                    if (!ReferenceEquals(cbSensor.SelectedItem, _vm.SelectedSensorOption))
                    {
                        cbSensor.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, _vm.SelectedSensorOption);
                    }
                    break;
            }
        }

        private void AnimateNeedle(double angle)
        {
            if (DialGeometry.NeedleAnimationMs <= 0)
            {
                NeedleRotate.Angle = angle;
                return;
            }

            var anim = new DoubleAnimation
            {
                To = angle,
                Duration = TimeSpan.FromMilliseconds(DialGeometry.NeedleAnimationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            NeedleRotate.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        // ------------------------------------------------------------------ metric panel slide in/out

        private const int SlideMs = 220;

        private void Ui_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_vm != null && e.PropertyName == nameof(SharedUiState.MetricsExpanded))
            {
                ApplyMetricsExpanded(_vm.Ui.MetricsExpanded, animate: IsLoaded);
            }
        }

        private void ApplyMetricsExpanded(bool expanded, bool animate)
        {
            MetricsHost.IsHitTestVisible = expanded;

            if (!animate)
            {
                MetricsHost.BeginAnimation(HeightProperty, null);
                MetricsHost.BeginAnimation(OpacityProperty, null);
                MetricsHost.Height = expanded ? double.NaN : 0;
                MetricsHost.Opacity = expanded ? 1 : 0;
                return;
            }

            // Animate between the current height and the panel's natural height (WPF can't animate to "Auto")
            double from = MetricsHost.ActualHeight;
            double width = MetricsHost.ActualWidth > 0 ? MetricsHost.ActualWidth : 236;
            MetricsPanel.Measure(new Size(width, double.PositiveInfinity));
            double to = expanded ? MetricsPanel.DesiredSize.Height : 0;

            var slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(SlideMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            if (expanded)
            {
                // Back to automatic height once fully open (so it follows content changes)
                slide.Completed += (s, e) =>
                {
                    if (_vm != null && _vm.Ui.MetricsExpanded)
                    {
                        MetricsHost.BeginAnimation(HeightProperty, null);
                        MetricsHost.Height = double.NaN;
                    }
                };
            }

            MetricsHost.BeginAnimation(HeightProperty, slide, HandoffBehavior.SnapshotAndReplace);
            MetricsHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(expanded ? 1 : 0, TimeSpan.FromMilliseconds(expanded ? SlideMs + 60 : SlideMs - 60)),
                HandoffBehavior.SnapshotAndReplace);
        }

        // ------------------------------------------------------------------ identify mode visuals

        private void ApplyIdentify(bool identifying)
        {
            if (identifying)
            {
                var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                IdentifyHalo.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                IdentifyHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            }
        }

        // ------------------------------------------------------------------ drag & drop reordering

        private const string DragFormat = "VU1WPF.DialViewModel";
        private static readonly Brush DropTargetBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x66, 0x2A));
        private static readonly Brush NormalBorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x28, 0x2C));
        private Point? _dragStart;

        private void DialBody_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Buttons on the dial (gear) handle their own clicks, so this only fires on the housing/screen
            if (e.ClickCount == 2)
            {
                // Double-click: identify this dial (flash its backlight)
                _dragStart = null;
                if (_vm != null) _vm.Host.Identify(_vm);
                e.Handled = true;
                return;
            }
            _dragStart = e.GetPosition(this);
        }

        private void DialBody_MouseMove(object sender, MouseEventArgs e)
        {
            if (_vm == null || _dragStart == null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragStart = null;
                return;
            }

            Vector delta = e.GetPosition(this) - _dragStart.Value;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragStart = null;
            var data = new DataObject(DragFormat, _vm);
            Opacity = 0.45;
            try
            {
                DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
            }
            finally
            {
                Opacity = 1;
            }
        }

        private DialViewModel? DraggedDial(DragEventArgs e) =>
            e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as DialViewModel : null;

        private void Card_DragEnter(object sender, DragEventArgs e) => Card_DragOver(sender, e);

        private void Card_DragOver(object sender, DragEventArgs e)
        {
            var source = DraggedDial(e);
            bool valid = source != null && _vm != null && !ReferenceEquals(source, _vm);
            e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
            CardBorder.BorderBrush = valid ? DropTargetBrush : NormalBorderBrush;
            e.Handled = true;
        }

        private void Card_DragLeave(object sender, DragEventArgs e)
        {
            CardBorder.BorderBrush = NormalBorderBrush;
        }

        private void Card_Drop(object sender, DragEventArgs e)
        {
            CardBorder.BorderBrush = NormalBorderBrush;
            var source = DraggedDial(e);
            if (source != null && _vm != null && !ReferenceEquals(source, _vm))
            {
                _vm.Host.MoveDial(source, _vm);
            }
            e.Handled = true;
        }

        // ------------------------------------------------------------------ buttons

        private void MetricsHeader_Click(object sender, RoutedEventArgs e) => _vm?.Host.ToggleMetrics();

        private void FinishSilver_Click(object sender, RoutedEventArgs e) => _vm?.Host.SetFinish(_vm, FinishTextures.Silver);

        private void FinishBlack_Click(object sender, RoutedEventArgs e) => _vm?.Host.SetFinish(_vm, FinishTextures.Black);

        private void Gear_Click(object sender, RoutedEventArgs e) => _vm?.Host.OpenImageEditor(_vm);

        private void Backlight_Click(object sender, RoutedEventArgs e) => _vm?.Host.OpenBacklight(_vm);

        // ------------------------------------------------------------------ metric

        private void cbSensor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_vm == null || cbSensor.SelectedItem is not SensorOption option) return;
            if (ReferenceEquals(option, _vm.SelectedSensorOption)) return;

            _vm.Host.ApplySensor(_vm, option);
        }

        // ------------------------------------------------------------------ scaling

        private void ShowScale(float min, float max)
        {
            txtMin.Text = min.ToString("0.###", CultureInfo.InvariantCulture);
            txtMax.Text = max.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void CommitScale()
        {
            if (_vm == null) return;
            var (min, max) = _vm.Host.ApplyScale(_vm, txtMin.Text, txtMax.Text);
            ShowScale(min, max);
        }

        private void Scale_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitScale();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _vm != null)
            {
                ShowScale(_vm.Dial.ScaleMin, _vm.Dial.ScaleMax);
                e.Handled = true;
            }
        }

        private void Scale_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitScale();

        private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var box = (TextBox)sender;
            e.Handled = !NumericInput.IsMatch(e.Text) || box.Text.Length >= 10;
        }

        // ------------------------------------------------------------------ rename

        private void NameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            NameEdit.Text = _vm.Name;
            NameText.Visibility = Visibility.Collapsed;
            NameEdit.Visibility = Visibility.Visible;
            NameEdit.Focus();
            NameEdit.SelectAll();
            e.Handled = true;
        }

        private async void EndRename(bool commit)
        {
            if (NameEdit.Visibility != Visibility.Visible) return;

            NameEdit.Visibility = Visibility.Collapsed;
            NameText.Visibility = Visibility.Visible;

            string newName = NameEdit.Text.Trim();
            if (commit && _vm != null && newName.Length > 0 && newName != _vm.Name)
            {
                await _vm.Host.RenameAsync(_vm, newName);
            }
        }

        private void NameEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { EndRename(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(false); e.Handled = true; }
        }

        private void NameEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(true);
    }
}
