using System.Windows;
using RentalServer.Core.TabletApps;

namespace RentalServer;

public partial class TabletAppAddWindow : Window
{
    public TabletAppDefinition? App { get; private set; }

    public TabletAppAddWindow()
    {
        InitializeComponent();
        CategoryComboBox.ItemsSource = CategoryOption.All;
        CategoryComboBox.SelectedValue = TabletAppCategories.Other;
    }

    public TabletAppAddWindow(TabletAppDefinition app)
        : this()
    {
        DisplayNameTextBox.Text = app.DisplayName;
        PackageNameTextBox.Text = app.PackageName;
        EnabledCheckBox.IsChecked = app.Enabled;
        DeepFreezeCheckBox.IsChecked = app.DeepFreeze;
        CategoryComboBox.SelectedValue = TabletAppCategories.Normalize(app.Category);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var app = new TabletAppDefinition(
            DisplayName: DisplayNameTextBox.Text.Trim(),
            PackageName: PackageNameTextBox.Text.Trim(),
            Enabled: EnabledCheckBox.IsChecked == true,
            DeepFreeze: DeepFreezeCheckBox.IsChecked == true,
            SortOrder: 0,
            Category: CategoryComboBox.SelectedValue?.ToString() ?? TabletAppCategories.Other);

        try
        {
            TabletAppConfigService.ValidateConfig(new TabletAppConfig([app]));
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        App = app;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed record CategoryOption(string DisplayName, string Value)
    {
        public static readonly IReadOnlyList<CategoryOption> All =
            TabletAppCategories.All
                .Select(c => new CategoryOption(TabletAppCategories.ToDisplayName(c), c))
                .ToArray();
    }
}
