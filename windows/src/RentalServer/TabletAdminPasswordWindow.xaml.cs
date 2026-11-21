using System.Windows;
using RentalServer.Core.TabletAdmin;

namespace RentalServer;

public partial class TabletAdminPasswordWindow : Window
{
    public string? Password { get; private set; }

    public TabletAdminPasswordWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password.Trim();
        var confirm = ConfirmPasswordBox.Password.Trim();

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            StatusText.Text = "Passwords do not match.";
            return;
        }

        try
        {
            TabletAdminConfigService.ValidatePassword(password);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        Password = password;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
