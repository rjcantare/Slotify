using System.Windows;
using System.Windows.Controls;
using MQTTnet;
using RentalClient.Windows.Configuration;

namespace RentalClient.Windows;

/// <summary>
/// First-run provisioning wizard. Collects station ID, device type, and broker
/// address, tests the MQTT connection, and saves appsettings.json. Runs before
/// the main rental client starts on a newly-imaged station.
/// </summary>
public partial class ProvisioningWindow : Window
{
    public ProvisioningWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Config written by the wizard. Null if the user closed without saving.
    /// </summary>
    public ClientConfig? Result { get; private set; }

    private async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatusText.Text = "Testing...";
        TestStatusText.Foreground = FindResource("SystemControlForegroundBaseMediumBrush") as System.Windows.Media.Brush
                                    ?? System.Windows.Media.Brushes.Gray;

        try
        {
            if (!TryParseInputs(out var config, out var error))
            {
                TestStatusText.Text = error;
                TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x53, 0x50));
                return;
            }

            var factory = new MqttClientFactory();
            using var client = factory.CreateMqttClient();
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(config.Broker.Host, config.Broker.Port)
                .WithClientId($"rental-provision-{Guid.NewGuid():N}")
                .WithCleanSession(true)
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(options, cts.Token);

            TestStatusText.Text = "Connected. Server reachable.";
            TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
            SaveButton.IsEnabled = true;
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            var msg = ex is OperationCanceledException
                ? "Connection timed out. Check the broker address and port."
                : $"Cannot reach broker at {BrokerHostBox.Text}:{BrokerPortBox.Text}.";
            TestStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x53, 0x50));
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!TryParseInputs(out var config, out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            config.SaveToDisk();
            Result = config;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Failed to save config: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private bool TryParseInputs(out ClientConfig config, out string error)
    {
        config = null!;
        error = string.Empty;

        var stationId = StationIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(stationId))
        {
            error = "Station ID is required.";
            return false;
        }

        var deviceType = ((ComboBoxItem)DeviceTypeBox.SelectedItem).Tag?.ToString() ?? "pc";

        var host = BrokerHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Broker host is required.";
            return false;
        }

        if (!int.TryParse(BrokerPortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            error = "Broker port must be a number between 1 and 65535.";
            return false;
        }

        config = new ClientConfig
        {
            StationId = stationId,
            DeviceType = deviceType,
            AppVersion = "1.1.0",
            Broker = new BrokerConfig { Host = host, Port = port },
            Debug = new DebugConfig { DisableLockOverlay = false },
            Connection = new ConnectionConfig { ReconnectIntervalSec = 5, OfflineUntrustedAfterSec = 120 },
            CountdownWarning = new CountdownWarningConfig
            {
                Enabled = true,
                FirstWarningSec = 60,
                FinalCountdownStartSec = 10
            }
        };

        config.ValidatePartial();
        return true;
    }
}
