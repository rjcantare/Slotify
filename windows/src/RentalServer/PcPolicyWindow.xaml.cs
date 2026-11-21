using System.Globalization;
using System.Windows;
using RentalServer.Core.Abuse;
using RentalServer.Core.PcPolicy;

namespace RentalServer;

public partial class PcPolicyWindow : Window
{
    public PcPolicyWindow(
        PcPolicyConfig pcConfig,
        AbusePreventionService.Config abuseConfig)
    {
        InitializeComponent();
        AutoCloseEnabledCheck.IsChecked = pcConfig.AutoCloseOnLockEnabled;
        IdleShutdownEnabledCheck.IsChecked = pcConfig.IdleShutdownEnabled;
        IdleMinutesBox.Text = pcConfig.IdleShutdownMinutes.ToString(CultureInfo.InvariantCulture);
        AbuseEnabledCheck.IsChecked = abuseConfig.Enabled;
        AbuseMaxBox.Text = abuseConfig.MaxEmptyClaims.ToString(CultureInfo.InvariantCulture);
        AbuseBanBox.Text = abuseConfig.BanMinutes.ToString(CultureInfo.InvariantCulture);
    }

    public PcPolicyConfig? EditedPcPolicyConfig { get; private set; }
    public AbusePreventionService.Config? EditedAbuseConfig { get; private set; }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IdleMinutesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            ValidationText.Text = "Enter idle minutes as a whole number.";
            return;
        }

        if (!int.TryParse(AbuseMaxBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxEmptyClaims))
        {
            ValidationText.Text = "Enter max empty claims as a whole number.";
            return;
        }

        if (!int.TryParse(AbuseBanBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var banMinutes))
        {
            ValidationText.Text = "Enter ban minutes as a whole number.";
            return;
        }

        var pcConfig = new PcPolicyConfig(
            AutoCloseOnLockEnabled: AutoCloseEnabledCheck.IsChecked == true,
            IdleShutdownEnabled: IdleShutdownEnabledCheck.IsChecked == true,
            IdleShutdownMinutes: minutes);
        var abuseConfig = new AbusePreventionService.Config(
            Enabled: AbuseEnabledCheck.IsChecked == true,
            MaxEmptyClaims: maxEmptyClaims,
            BanMinutes: banMinutes);

        try
        {
            PcPolicyConfigService.ValidateConfig(pcConfig);
            AbusePreventionService.ValidateConfig(abuseConfig);
        }
        catch (ArgumentException ex)
        {
            ValidationText.Text = ex.Message;
            return;
        }

        EditedPcPolicyConfig = pcConfig;
        EditedAbuseConfig = abuseConfig;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
