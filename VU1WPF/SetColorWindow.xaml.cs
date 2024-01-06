using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
    public partial class SetColorWindow : Window
    {
        protected MainWindow mainWindow { get; set; }

        public void SetMainWindow(MainWindow mw)
        {
            this.mainWindow = mw;
        }

        public SetColorWindow()
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

        private void btnSetColor_click(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null)
            {
                return;
            }

            int red = (int)sliderRed.Value;
            int green = (int)sliderGreen.Value;
            int blue = (int)sliderBlue.Value;

            mainWindow.Selected_Dial_Change_Color(red, green, blue);
        }
    }
}
