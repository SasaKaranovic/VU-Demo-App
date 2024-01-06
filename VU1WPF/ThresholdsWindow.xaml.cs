using KR_VU1_ConfigurationManager;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace VU1WPF
{
    public partial class ThresholdsWindow : Window
    {
        protected MainWindow mainWindow { get; set; }
        protected ClassConfigurationManager ConfigManager { get; set; }

        public void SetMainWindow(MainWindow mw)
        {
            this.mainWindow = mw;
            cbThresholds.ItemsSource = mainWindow.gCurrentlySelectedDial.Thresholds;
            Trace.WriteLine(mainWindow.gCurrentlySelectedDial.Thresholds);
        }

        public void SetConfigManager(ClassConfigurationManager cm)
        {
            this.ConfigManager = cm;
        }

        public ThresholdsWindow()
        {
            InitializeComponent();

            cbPreset.ItemsSource = BacklightPresets.AvailablePresets;
        }

        private void cbBacklightPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            sliderRed.Value = BacklightPresets.AvailablePresets[cbPreset.SelectedIndex].Red;
            sliderGreen.Value = BacklightPresets.AvailablePresets[cbPreset.SelectedIndex].Green;
            sliderBlue.Value = BacklightPresets.AvailablePresets[cbPreset.SelectedIndex].Blue;
        }

        private void cbRuleSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbThresholds.SelectedItem is ClassDialThreshold selected)
            {
                txtValue.Text = selected.Threshold.ToString();
                sliderRed.Value = selected.BacklightRed;
                sliderGreen.Value = selected.BacklightGreen;
                sliderBlue.Value = selected.BacklightBlue;
            }

        }

        private void btnSetThreshold_click(object sender, RoutedEventArgs e)
        {
            int value = ParseMinMaxValue(txtValue.Text, 0);
            int red = (int)sliderRed.Value;
            int green = (int)sliderGreen.Value;
            int blue = (int)sliderBlue.Value;

            if (mainWindow != null )
            {
                // Create new threshold
                ClassDialThreshold newThreshold = new() { 
                    Threshold = value,
                    BacklightRed = red,
                    BacklightGreen = green,
                    BacklightBlue = blue 
                };

                // Check if it already exists
                int index = mainWindow.gCurrentlySelectedDial.Thresholds.FindIndex(item => item.Threshold == value);
                if (index >= 0)
                {
                    mainWindow.gCurrentlySelectedDial.Thresholds[index] = newThreshold;
                }
                // Otherwise add new
                else
                {
                    mainWindow.gCurrentlySelectedDial.Thresholds.Add(newThreshold);
                }

                ConfigManager.UpdateDialConfig(mainWindow.gCurrentlySelectedDial, true);
                sortThresholds();
            }
            else
            {
                Trace.WriteLine("Window owner is not set!");
            }
            
        }

        private void btnDeleteThreshold_click(object sender, RoutedEventArgs e)
        {
            if (mainWindow != null)
            {
                // Check if value is selected
                if (cbThresholds.SelectedItem != null)
                {
                    mainWindow.gCurrentlySelectedDial.Thresholds.Remove((ClassDialThreshold) cbThresholds.SelectedItem);
                    ConfigManager.UpdateDialConfig(mainWindow.gCurrentlySelectedDial, true);
                    sortThresholds();
                }
            }
            else
            {
                Trace.WriteLine("Window owner is not set!");
            }

        }

        private int ParseMinMaxValue(String value, int def)
        {
            int retValue = def;
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out retValue);

            if (retValue < 0)
            {
                retValue = 0;
            }
            else if (retValue > 100)
            {
                retValue = 100;
            }
            return retValue;
        }

        void NumericTextBoxInput(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex(@"^[0-9]*(?:[0-9]*)?$");

            // Limit lenght to 6 characters
            if (((System.Windows.Controls.TextBox)sender).Text.Length >= 6)
            {
                e.Handled = true;
            }
            // Regex match
            else if (regex.IsMatch(e.Text) && !(e.Text == "." && ((System.Windows.Controls.TextBox)sender).Text.Contains(e.Text)))
            {
                e.Handled = false;
            }
            else
            {
                e.Handled = true;
            }
        }


        private void thresholdWindow_loaded(object sender, RoutedEventArgs e)
        {
            // Pause dial value and backlight update
            if(mainWindow != null)
            {
                //mainWindow.PauseDialUpdate();
                sortThresholds();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Resume dial value and backlight update
            if (mainWindow != null)
            {
                //mainWindow.ResumeDialUpdate();
            }
        }

        private void sortThresholds()
        {
            if (mainWindow != null)
            {
                int tmp_selection = cbThresholds.SelectedIndex;
                cbThresholds.ItemsSource = mainWindow.gCurrentlySelectedDial.Thresholds;
                cbThresholds.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Threshold", System.ComponentModel.ListSortDirection.Ascending));
                cbThresholds.Items.Refresh();

                if (tmp_selection < 0)
                {
                    tmp_selection = 0;
                }
                cbThresholds.SelectedIndex = tmp_selection;
                
                // Sort thresholds in main window
                mainWindow.sortDialThresholds();
            }
        }
    }
}
