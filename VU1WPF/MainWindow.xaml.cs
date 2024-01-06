using System;
using System.Collections.Generic;
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
using System.Windows.Navigation;
using System.Windows.Shapes;
using static VU1WPF.ClassDialGUI;
using KR_VU1_Server;
using LibreHardwareMonitor.Hardware;
using MaterialDesignThemes.Wpf;
using System.Diagnostics;
using LibreHardwareMonitor.Hardware.Cpu;
using System.Windows.Threading;
using NullSoftware.ToolKit;
using static KR_VU1_Sensors.ClassVUSensors;
using KR_VU1_ConfigurationManager;
using System.ComponentModel;
using System.Windows.Forms;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Globalization;
using YamlDotNet.Core.Tokens;
using System.Runtime.CompilerServices;

namespace VU1WPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public object FormWindowState { get; private set; }
        List<VU1_Sensor> computerSensors = new List<VU1_Sensor>();
        public ClassConfigurationManager ConfigManager;
        VU1_SensorManager SensorManager;
        VU1_Server DialServer;
        public List<ClassDialGUI> gDials = new List<ClassDialGUI>();
        public ClassDialGUI gCurrentlySelectedDial = new ClassDialGUI { FriendlyName = "", UID = "" };
        bool gDialUpdatePaused = false;


        public MainWindow()
        {
            InitializeComponent();

            // Create configuration manager instance
            ConfigManager = new ClassConfigurationManager();


            // Create instance of VU1 server
            string MasterKey = ConfigManager.GetMasterKey();
            DialServer = new VU1_Server(MasterKey);

            // Initiate sensor manager
            SensorManager = new VU1_SensorManager();
            computerSensors = SensorManager.get_used_sensors();

            // Bind UI and back-end items
            lbDials.ItemsSource = gDials;

            // Bind UI sensor ComboBox to sensor list
            cbDialMetric.ItemsSource = computerSensors;
            cbDialMetricCategory.ItemsSource = SensorManager.UsedSensors;


            // Add drag
            //cnvsTitleCanvas.MouseDown += delegate { DragMove(); };
            lblTitle.MouseDown += delegate { DragMove(); };

            RefreshDialList();
            //SetAllDialValue(50);

            //Systray icon
            System.Windows.Forms.NotifyIcon notifyIcon;
            notifyIcon = new System.Windows.Forms.NotifyIcon();
            notifyIcon.DoubleClick += notifyIcon_DoubleClick;
            notifyIcon.Icon = new System.Drawing.Icon("VU1_Icon.ico");
            notifyIcon.Visible = true;

            // Create timer
            float dialUpdatePeriod = ConfigManager.GetDialUpdatePeriod();
            //DispatcherTimer timer = new DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send);
            DispatcherTimer timer = new DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render);
            timer.Tick += TimerTick;
            timer.Interval = TimeSpan.FromSeconds(dialUpdatePeriod);
            timer.Start();

            Trace.WriteLine(String.Format("Dials updated every {0} seconds.", dialUpdatePeriod));
        }

        private void btnX_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void btnMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void btnMinToTray_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
            this.Hide();
        }

        void notifyIcon_DoubleClick(object sender, EventArgs e)
        {
            this.Show();
            WindowState = WindowState.Normal;
            Trace.WriteLine("Systray double click event");
        }


        private void lbDials_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ClassDialGUI sel = lbDials.SelectedItem as ClassDialGUI;
            if (sel == null) return;


            gCurrentlySelectedDial = lbDials.SelectedItem as ClassDialGUI;

            txtlSelectedDialName.Text = sel.FriendlyName.ToString();
            lblSelectedDialUID.Content = sel.UID.ToString();
            if (gCurrentlySelectedDial.Sensor != null)
            {
                lblCurrentMetric.Content = String.Format("{0} - {1} - {2}", gCurrentlySelectedDial.Sensor.Name.ToString(), gCurrentlySelectedDial.Sensor.SensorType.ToString(), gCurrentlySelectedDial.Sensor.Identifier.ToString());
                //lblCurrentValue.Content = gCurrentlySelectedDial.Sensor.Value.ToString();
                lblScalingMin.Content = gCurrentlySelectedDial.ScaleMin.ToString();
                lblScalingMax.Content = gCurrentlySelectedDial.ScaleMax.ToString();
                txtMinValue.Text = gCurrentlySelectedDial.ScaleMin.ToString();
                txtMaxValue.Text = gCurrentlySelectedDial.ScaleMax.ToString();
                
            }
            else
            {
                lblCurrentMetric.Content = "";
                lblCurrentValue.Content = "";
                lblCurrentPercent.Content = "";
                lblScalingMin.Content = "";
                lblScalingMax.Content = "";
                txtMinValue.Text = "0";
                txtMaxValue.Text = "100";
            }
            
        }

        private ClassDialGUI CreateGUIDial(String UID, String FriendlyName)
        {
            Trace.WriteLine(String.Format("Adding dial UID:{0} Name:{1}", UID, FriendlyName));
            ClassDialGUI tmpDial = new ClassDialGUI() { FriendlyName = FriendlyName, UID = UID };

            // Try to read sensor identifier from config
            string sensorIdentifier = ConfigManager.GetDialMetric(UID);
            float sensorMin = ConfigManager.GetDialMin(UID);
            float sensorMax = ConfigManager.GetDialMax(UID);
            List<ClassDialThreshold> thresholds = ConfigManager.GetDialThresholds(UID);

            if (sensorIdentifier != "")
            {
                VU1_Sensor tmpSensor = SensorManager.FindSensorByIdentifier(sensorIdentifier);
                if(tmpSensor != null)
                {
                    tmpDial.Sensor = tmpSensor.Sensor;
                    tmpDial.SensorIdentifier = tmpSensor.Sensor.Identifier.ToString();
                    tmpDial.SensorName = tmpSensor.Sensor.Name.ToString();
                    tmpDial.ScaleMin = sensorMin;
                    tmpDial.ScaleMax = sensorMax;
                    tmpDial.Thresholds = thresholds;
                }
            }
            return tmpDial;
        }

        private void RefreshDialList()
        {
            Trace.WriteLine("Refreshing dial list");
            DialServer.RefreshDialList();
            List<DialInfo> restDials = DialServer.GetDialList();

            Trace.WriteLine("Updatig local dials");
            gDials.Clear();
            foreach (DialInfo dial in restDials)
            {
                ClassDialGUI tmpDial = CreateGUIDial(dial.uid, dial.dial_name);
                gDials.Add(tmpDial);
            }

            Trace.WriteLine("Updating GUI dials");
            lbDials.ItemsSource = gDials;
            lbDials.Items.SortDescriptions.Clear();
            lbDials.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("FriendlyName", System.ComponentModel.ListSortDirection.Ascending));
            lbDials.Items.Refresh();

            // Sort dial thresholds
            sortDialThresholds();
        }

        public void PauseDialUpdate()
        {
            gDialUpdatePaused = true;
            btnToggleDialUpdate.Content = new PackIcon { Kind = PackIconKind.Play };
        }

        public void ResumeDialUpdate()
        {
            gDialUpdatePaused = false;
            btnToggleDialUpdate.Content = new PackIcon { Kind = PackIconKind.Pause };

        }

        private void btnRefreshDials_click(object sender, RoutedEventArgs e)
        {
            RefreshDialList();
        }

        private void btnToggleDialUpdate_click(object sender, RoutedEventArgs e)
        {
            if(gDialUpdatePaused)
            {
                ResumeDialUpdate();
            }
            else
            {
                PauseDialUpdate();
            }
        }

        private float ParseMinMaxValue(String value, float def)
        {
            float fValue = def;
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fValue);
            return fValue;
        }

        private void btnSaveDialSettings_click(object sender, RoutedEventArgs e)
        {
            if (gCurrentlySelectedDial == null)
            {
                return;
            }

            // Prepare data
            string newName = txtlSelectedDialName.Text;
            float minValue = ParseMinMaxValue(txtMinValue.Text, 100);
            float maxValue = ParseMinMaxValue(txtMaxValue.Text, 100);

            // Minimum can not be bigger than maximum
            if (minValue > maxValue)
            {
                float tpm = minValue;
                minValue = maxValue;
                maxValue = tpm;
            }
            
            // Update currently selected sensor
            gCurrentlySelectedDial.FriendlyName = newName;
            gCurrentlySelectedDial.ScaleMin = minValue;
            gCurrentlySelectedDial.ScaleMax = maxValue;

            // API Call to update dial name
            if (DialServer.UpdateDialName(gCurrentlySelectedDial.UID, newName))
            {
                lbDials.Items.SortDescriptions.Clear();
                lbDials.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("FriendlyName", System.ComponentModel.ListSortDirection.Ascending));
                lbDials.Items.Refresh();
            }

            // Update config/UI only if:
            // - valid sensor is selected from the drop-down
            // - valud sensor is stored in config, then update only scaling values
            if (cbDialMetric.SelectedItem != null || gCurrentlySelectedDial.Sensor != null)
            {
                // New sensor is selected
                if (cbDialMetric.SelectedItem != null)
                {
                    //gCurrentlySelectedDial.Sensor = computerSensors[cbDialMetric.SelectedIndex].Sensor;
                    VU1_Sensor selectedSensor = cbDialMetric.SelectedItem as VU1_Sensor;
                    gCurrentlySelectedDial.Sensor = selectedSensor.Sensor;
                    lblCurrentMetric.Content = String.Format("{0} - {1} - {2}", gCurrentlySelectedDial.Sensor.Name.ToString(), gCurrentlySelectedDial.Sensor.SensorType.ToString(), gCurrentlySelectedDial.Sensor.Identifier.ToString());
                }

                lblCurrentValue.Content = gCurrentlySelectedDial.Sensor.Value.ToString();
                lblScalingMin.Content = gCurrentlySelectedDial.ScaleMin.ToString();
                lblScalingMax.Content = gCurrentlySelectedDial.ScaleMax.ToString();

                // Update config file
                ConfigManager.UpdateDialConfig(gCurrentlySelectedDial, true);
            }
            
        }

        private void cbDialMetricCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbDialMetricCategory.SelectedItem == null)
            {
                return;
            }

            List<VU1_Sensor> filtered = new List<VU1_Sensor> { };

            foreach (var sens in computerSensors)
            {

                if (sens.Sensor.SensorType.ToString().Contains(cbDialMetricCategory.SelectedItem.ToString()))
                {
                    filtered.Add(sens);
                }
            }

            cbDialMetric.ItemsSource = filtered;
            cbDialMetric.Items.Refresh();
        }

        private void btnChangeImage_click(object sender, RoutedEventArgs e)
        {
            if (gCurrentlySelectedDial == null || gCurrentlySelectedDial.UID == "")
            {
                return;
            }

            //Create a new instance of openFileDialog
            OpenFileDialog res = new OpenFileDialog();

            //Filter
            res.Filter = "Image Files|*.jpg;*.jpeg;*.png";

            //When the user select the file
            if (res.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DialServer.UpdateDialBackgroundImage(gCurrentlySelectedDial.UID, res.FileName);
            }
            
        }

        private void btnSetDialRules_click(object sender, RoutedEventArgs e)
        {
            if (gCurrentlySelectedDial == null || gCurrentlySelectedDial.UID == "")
            {
                return;
            }

            ThresholdsWindow thresholdsWindow = new ThresholdsWindow();
            thresholdsWindow.Owner = this;
            thresholdsWindow.SetMainWindow(this);
            thresholdsWindow.SetConfigManager(ConfigManager);
            thresholdsWindow.ShowDialog();
        }

        private void btnChangeDialColor_click(object sender, RoutedEventArgs e)
        {
            if (gCurrentlySelectedDial == null || gCurrentlySelectedDial.UID == "")
            {
                return;
            }

            SetColorWindow setColorWindow = new SetColorWindow();
            setColorWindow.Owner = this;
            setColorWindow.SetMainWindow(this);
            setColorWindow.Show();
        }

        public void Selected_Dial_Change_Color(int red, int green, int blue)
        {
            DialServer.UpdateDialBacklight(gCurrentlySelectedDial.UID, red, green, blue);
        }

        private void btnAbout_click(object sender, RoutedEventArgs e)
        {
            AboutWindow aboutWindow = new AboutWindow();
            aboutWindow.Show();
        }


        void NumericTextBoxInput(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex(@"^[0-9]*(?:\.[0-9]*)?$");

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

        public void SaveDialConfig()
        {
            ConfigManager.SaveConfigFile();
        }


        private void SetAllDialValue(int val)
        {
            foreach (ClassDialGUI dial in gDials)
            {
                DialServer.UpdateDialValue(dial.UID, val);
            }
        }

        private void RefreshDialMetric(ClassDialGUI dial)
        {
            // Refresh dial sensor value
            if (dial.Sensor != null)
            {
                // Initial dial value
                int dialValue = 0;
                int dialRed = 0;
                int dialGreen = 0;
                int dialBlue = 0;
                bool bBacklightUpdate = false;

                // Reload sensor
                dial.Sensor.Hardware.Update();

                // Fetch sensor value
                float fSensorValue = dial.Sensor.Value ?? 0;

                // Edge case: If for whatever reason scale min and scale max are the same, we will show 0%
                // Value is less than scale min means dial shows 0%
                if ( (dial.ScaleMin == dial.ScaleMax) || (fSensorValue <= dial.ScaleMin) )
                {
                    dialValue = 0;
                }
                // Value is greater than scale max means dial shows 100%
                else if (fSensorValue >= dial.ScaleMax)
                {
                    dialValue = 100;
                }
                // Value is in between scale min and max. Convert to appropriate percent value
                else
                {
                    float mult = (dial.ScaleMax - dial.ScaleMin) / 100;
                    if (mult <= 0)
                    {
                        Trace.WriteLine(String.Format("Scale multiplier calculated as {0}. Clipping to 0.001", mult));
                        mult = 0.001f;
                    }

                    dialValue = ((int)((int)Math.Round(fSensorValue - dial.ScaleMin) / mult));

                    // Clip dial value
                    if (dialValue <0 )
                    {
                        Trace.WriteLine(String.Format("Sensor value {0}. Clipping to 0.", dial));
                        dialValue = 0;
                    }

                }

                // Update backlight based on defined thresholds
                if (dial.Thresholds != null && dial.Thresholds.Count > 0)
                {
//                    ClassDialThreshold match = dial.Thresholds.FindLast(item => item.Threshold <= dialValue);
                    ClassDialThreshold match = dial.Thresholds.Find(item => item.Threshold >= dialValue);

                    if (match != null)
                    {
                        dialRed = match.BacklightRed;
                        dialGreen = match.BacklightGreen;
                        dialBlue = match.BacklightBlue;
                        bBacklightUpdate = true;
                    }
                }


                // Check if we need to update GUI
                if (gCurrentlySelectedDial.Sensor != null)
                {
                    if (gCurrentlySelectedDial.Sensor.Identifier == dial.Sensor.Identifier)
                    {
                        lblCurrentPercent.Content = String.Format("{0}%", dialValue);
                        lblCurrentValue.Content = String.Format("[{0:0.000}]", fSensorValue);
                    }
                }

                Trace.WriteLine(String.Format("Dial:{0} set to {1}% [Sensor: {2}] - Raw value: {1}", dial.UID, dialValue, dial.Sensor.Identifier, fSensorValue));
                DialServer.UpdateDialValue(dial.UID, dialValue);

                if(bBacklightUpdate)
                {
                    DialServer.UpdateDialBacklight(dial.UID, dialRed, dialGreen, dialBlue);
                    bBacklightUpdate = false;
                }
            }
        }

        public void sortDialThresholds()
        {
            foreach (ClassDialGUI dial in gDials)
            {
                dial.Thresholds = dial.Thresholds.OrderBy(item => item.Threshold).ToList();
            }
        }


        private void mainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Reset each dial
            foreach (ClassDialGUI dial in gDials)
            {
                DialServer.UpdateDialValue(dial.UID, 0);
                DialServer.UpdateDialBacklight(dial.UID, 0, 0, 0);
            }
        }


        private void TimerTick(object sender, EventArgs e)
        {
            // Don't run if pause has been requested
            if (gDialUpdatePaused) return;

            // Update each dial sensor
            foreach (ClassDialGUI dial in gDials)
            {
                if (dial.Sensor != null)
                {
                    RefreshDialMetric(dial);
                }
            }
        }

    }
}
