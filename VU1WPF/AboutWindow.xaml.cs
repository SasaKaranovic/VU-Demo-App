using System;
using System.Diagnostics;
using System.Windows;
using Serilog;

namespace VU1WPF
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            DarkTitleBar.Apply(this);

            txtBuildStamp.Text = $"Build {Properties.Resources.BuildDate.Trim()}  ·  .NET {Environment.Version}";
        }

        private void btnWebsite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://vudials.com") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open website");
            }
        }

        private void btnCloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
