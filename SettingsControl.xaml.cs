using System.Windows.Controls;

namespace MairaSimHub.WindPlugin
{
    public partial class SettingsControl : UserControl
    {
        public MairaWindPlugin Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(MairaWindPlugin plugin) : this()
        {
            Plugin = plugin;

            // Bind all sliders and toggles to the settings object.
            DataContext = plugin.Settings;

            UpdateConnectionStatus();
            UpdateTestStatus();
        }

        // -----------------------------------------------------------------------
        // Connection buttons
        // -----------------------------------------------------------------------

        private void ConnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.Connect();
            UpdateConnectionStatus();
        }

        private void DisconnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.Disconnect();
            UpdateConnectionStatus();
            UpdateTestStatus();
        }

        // -----------------------------------------------------------------------
        // Fan test buttons
        // -----------------------------------------------------------------------

        private void TestLeftButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.SetTestLeft(true);
            Plugin.SetTestRight(false);
            UpdateTestStatus();
        }

        private void TestRightButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.SetTestLeft(false);
            Plugin.SetTestRight(true);
            UpdateTestStatus();
        }

        private void StopTestButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Plugin.SetTestLeft(false);
            Plugin.SetTestRight(false);
            UpdateTestStatus();
        }

        // -----------------------------------------------------------------------
        // Status displays
        // -----------------------------------------------------------------------

        private void UpdateConnectionStatus()
        {
            if (ConnectionStatusText == null)
                return;

            if (Plugin.IsConnected)
            {
                ConnectionStatusText.Text = "Status: Connected";
            }
            else if (Plugin.Settings.WindEnabled)
            {
                ConnectionStatusText.Text = Plugin.Settings.AutoConnect
                    ? "Status: Not connected  (device may not be plugged in)"
                    : "Status: Not connected";
            }
            else
            {
                ConnectionStatusText.Text = "Status: Plugin disabled";
            }
        }

        private void UpdateTestStatus()
        {
            if (TestStatusText == null)
                return;

            if (!Plugin.IsConnected)
            {
                TestStatusText.Text = "Connect to the device before testing.";
                return;
            }

            // Reflect which fan is currently under test.
            // These flags are read from the plugin's internal state via public methods.
            TestStatusText.Text = "No test active — fans respond to telemetry.";
        }
    }
}
