using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using RentalServer.Core.TabletApps;

namespace RentalServer;

public partial class TabletAppEditorWindow : Window
{
    private readonly ObservableCollection<TabletAppEditorRow> _rows = new();
    private readonly IReadOnlyList<TabletAppInventoryRecord> _inventories;

    public TabletAppConfig? EditedConfig { get; private set; }

    public TabletAppEditorWindow(
        TabletAppConfig current,
        IReadOnlyList<TabletAppInventoryRecord>? inventories = null)
    {
        InitializeComponent();
        _inventories = inventories ?? Array.Empty<TabletAppInventoryRecord>();

        foreach (var app in current.Apps.OrderBy(a => a.SortOrder))
        {
            _rows.Add(TabletAppEditorRow.FromDefinition(app));
        }

        AppGrid.ItemsSource = CollectionViewSource.GetDefaultView(_rows);
        CollectionViewSource.GetDefaultView(_rows).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(TabletAppEditorRow.CategoryName)));
        if (AppGrid.Columns.OfType<System.Windows.Controls.DataGridComboBoxColumn>().FirstOrDefault() is { } categoryColumn)
        {
            categoryColumn.ItemsSource = CategoryOption.All;
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new TabletAppAddWindow { Owner = this };
        var saved = dialog.ShowDialog() == true;
        if (!saved || dialog.App is null)
        {
            return;
        }

        var row = TabletAppEditorRow.FromDefinition(dialog.App);
        _rows.Add(row);
        AppGrid.SelectedItem = row;
        AppGrid.ScrollIntoView(row);
        StatusText.Text = string.Empty;
    }

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (AppGrid.SelectedItem is not TabletAppEditorRow row)
        {
            StatusText.Text = "Select an app to edit.";
            return;
        }

        var dialog = new TabletAppAddWindow(row.ToDefinition(sortOrder: 0)) { Owner = this };
        var saved = dialog.ShowDialog() == true;
        if (!saved || dialog.App is null)
        {
            return;
        }

        row.Apply(dialog.App);
        AppGrid.Items.Refresh();
        AppGrid.SelectedItem = row;
        AppGrid.ScrollIntoView(row);
        StatusText.Text = string.Empty;
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        if (_inventories.Count == 0)
        {
            StatusText.Text = "No tablet app list received yet. Make sure the tablet is online, then reopen Tablet Apps.";
            return;
        }

        var existingPackages = _rows
            .Select(r => r.PackageName.Trim())
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var dialog = new TabletAppImportWindow(_inventories, existingPackages) { Owner = this };
        var saved = dialog.ShowDialog() == true;
        if (!saved || dialog.SelectedApps.Count == 0)
        {
            return;
        }

        foreach (var app in dialog.SelectedApps)
        {
            if (_rows.Any(r => string.Equals(r.PackageName.Trim(), app.PackageName, StringComparison.Ordinal)))
            {
                continue;
            }

            _rows.Add(TabletAppEditorRow.FromDefinition(app));
        }

        StatusText.Text = $"Imported {dialog.SelectedApps.Count} app(s). Click Save to publish.";
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (AppGrid.SelectedItem is TabletAppEditorRow row)
        {
            _rows.Remove(row);
        }
    }

    private void OnRemoveAllClicked(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Remove all {_rows.Count} app(s) from the tablet whitelist?",
            "Remove All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _rows.Clear();
        StatusText.Text = "All apps removed. Click Save to publish.";
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var apps = _rows
                .Select((row, index) => row.ToDefinition(index))
                .ToArray();
            var config = new TabletAppConfig(apps);
            TabletAppConfigService.ValidateConfig(config);
            EditedConfig = config;
            DialogResult = true;
            Close();
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class TabletAppEditorRow
    {
        public string DisplayName { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string Category { get; set; } = TabletAppCategories.Other;
        public bool Enabled { get; set; }
        public bool DeepFreeze { get; set; }

        public string CategoryName =>
            TabletAppCategories.ToDisplayName(Category);

        public static TabletAppEditorRow FromDefinition(TabletAppDefinition app)
        {
            return new TabletAppEditorRow
            {
                DisplayName = app.DisplayName,
                PackageName = app.PackageName,
                Category = TabletAppCategories.Normalize(app.Category),
                Enabled = app.Enabled,
                DeepFreeze = app.DeepFreeze
            };
        }

        public void Apply(TabletAppDefinition app)
        {
            DisplayName = app.DisplayName;
            PackageName = app.PackageName;
            Category = TabletAppCategories.Normalize(app.Category);
            Enabled = app.Enabled;
            DeepFreeze = app.DeepFreeze;
        }

        public TabletAppDefinition ToDefinition(int sortOrder)
        {
            return new TabletAppDefinition(
                DisplayName: DisplayName.Trim(),
                PackageName: PackageName.Trim(),
                Enabled: Enabled,
                DeepFreeze: DeepFreeze,
                SortOrder: sortOrder,
                Category: TabletAppCategories.Normalize(Category));
        }
    }

    private sealed record CategoryOption(string DisplayName, string Value)
    {
        public static readonly IReadOnlyList<CategoryOption> All =
            TabletAppCategories.All
                .Select(c => new CategoryOption(TabletAppCategories.ToDisplayName(c), c))
                .ToArray();
    }
}
