using System.Windows;

namespace VU1WPF
{
    /// <summary>Shown when the external VU1 server can't be reached: offers to start the built-in server.</summary>
    public partial class ServerChoiceWindow : Window
    {
        public ServerChoiceWindow(string serverUrl)
        {
            InitializeComponent();
            DarkTitleBar.Apply(this);
            txtMessage.Text = $"No VU1 server is answering at {serverUrl}.";
        }

        /// <summary>User ticked "Always use the built-in server".</summary>
        public bool RememberChoice => chkRemember.IsChecked == true;

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
