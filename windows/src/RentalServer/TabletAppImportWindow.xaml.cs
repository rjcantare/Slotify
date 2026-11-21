using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows;
using RentalServer.Core.TabletApps;

namespace RentalServer;

public partial class TabletAppImportWindow : Window
{
    private readonly ObservableCollection<ImportRow> _rows = new();

    public IReadOnlyList<TabletAppDefinition> SelectedApps { get; private set; } =
        Array.Empty<TabletAppDefinition>();

    public TabletAppImportWindow(
        IReadOnlyList<TabletAppInventoryRecord> inventories,
        IReadOnlySet<string> existingPackages)
    {
        InitializeComponent();

        var latest = inventories
            .OrderByDescending(i => i.PublishedAt, StringComparer.Ordinal)
            .First();

        InventoryText.Text = $"From {latest.StationId}: {latest.Apps.Count} launchable app(s)";

        foreach (var app in latest.Apps)
        {
            // Skip apps already whitelisted and protected/system-critical
            // packages (Settings, the kiosk app, launchers, …). Protected
            // packages can never be imported, so they are not shown at all —
            // that is why this screen no longer needs a "Blocked" column.
            if (existingPackages.Contains(app.PackageName) ||
                TabletAppClassifier.IsProtectedPackage(app.PackageName))
            {
                continue;
            }

            var category = TabletAppClassifier.InferCategory(app.DisplayName, app.PackageName);
            _rows.Add(new ImportRow
            {
                Include = true,
                // Deep-freeze clears the app's data at session end. For games
                // that wipes saved progress with no logout benefit (customers
                // log out their own accounts — see backlog P2 logout decision),
                // so games default OFF; everything else keeps the prior default.
                DeepFreeze = category != TabletAppCategories.Games,
                DisplayName = app.DisplayName,
                PackageName = app.PackageName,
                Category = category
            });
        }

        var view = CollectionViewSource.GetDefaultView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ImportRow.CategoryName)));

        // Live grouping re-homes a row to its new category group the moment the
        // operator changes its Category combo — without a manual view Refresh.
        // The old CurrentCellChanged→Refresh regenerated row containers on every
        // cell change, which fought single-click checkbox toggling.
        if (view is ICollectionViewLiveShaping live && live.CanChangeLiveGrouping)
        {
            live.LiveGroupingProperties.Add(nameof(ImportRow.CategoryName));
            live.IsLiveGrouping = true;
        }

        ImportGrid.ItemsSource = view;
        if (ImportGrid.Columns.OfType<System.Windows.Controls.DataGridComboBoxColumn>().FirstOrDefault() is { } categoryColumn)
        {
            categoryColumn.ItemsSource = CategoryOption.All;
        }
        if (_rows.Count == 0)
        {
            StatusText.Text = "No new apps to import from this tablet.";
        }
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var selected = _rows
            .Where(r => r.Import)
            .Select((r, index) => new TabletAppDefinition(
                DisplayName: r.DisplayName.Trim(),
                PackageName: r.PackageName.Trim(),
                Enabled: r.Enabled,
                DeepFreeze: r.DeepFreeze,
                SortOrder: index,
                Category: TabletAppCategories.Normalize(r.Category)))
            .ToArray();

        if (selected.Length == 0)
        {
            StatusText.Text = "Check at least one app to import.";
            return;
        }

        SelectedApps = selected;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSelectAllClicked(object sender, RoutedEventArgs e) =>
        SetImportForRows(_rows, true);

    private void OnUncheckAllClicked(object sender, RoutedEventArgs e) =>
        SetImportForRows(_rows, false);

    private void OnSelectGamesClicked(object sender, RoutedEventArgs e) =>
        ToggleCategory(TabletAppCategories.Games);

    private void OnSelectVideoSocialClicked(object sender, RoutedEventArgs e) =>
        ToggleCategory(TabletAppCategories.VideoSocial);

    private void OnSelectBrowserClicked(object sender, RoutedEventArgs e) =>
        ToggleCategory(TabletAppCategories.Browser);

    private void OnSelectToolsClicked(object sender, RoutedEventArgs e) =>
        ToggleCategory(TabletAppCategories.Tools);

    private void OnSelectOtherClicked(object sender, RoutedEventArgs e) =>
        ToggleCategory(TabletAppCategories.Other);

    private void ToggleCategory(string category)
    {
        var normalized = TabletAppCategories.Normalize(category);
        var rows = _rows.Where(r => TabletAppCategories.Normalize(r.Category) == normalized).ToArray();
        var shouldInclude = rows.Any(r => !r.Include);
        SetImportForRows(rows, shouldInclude);
    }

    private void SetImportForRows(IEnumerable<ImportRow> rows, bool value)
    {
        // One toggle: Include drives both Import and Show. INotifyPropertyChanged
        // pushes the new state to the bound checkboxes, so no grid Refresh (which
        // would interfere with single-click toggling) is needed.
        foreach (var row in rows)
        {
            row.Include = value;
        }
    }

    private sealed class ImportRow : INotifyPropertyChanged
    {
        private bool _include;
        private bool _deepFreeze;
        private string _category = TabletAppCategories.Other;

        // The single operator-facing toggle. Importing an app always shows it on
        // the tablet, so this one flag drives both underlying model fields.
        public bool Include
        {
            get => _include;
            set => SetField(ref _include, value);
        }

        // Underlying fields OnImportClicked reads when building the
        // TabletAppDefinition. Both follow Include.
        public bool Import => _include;
        public bool Enabled => _include;

        public bool DeepFreeze
        {
            get => _deepFreeze;
            set => SetField(ref _deepFreeze, value);
        }

        public string Category
        {
            get => _category;
            set
            {
                if (SetField(ref _category, value))
                {
                    OnPropertyChanged(nameof(CategoryName));
                }
            }
        }

        public string CategoryName => TabletAppCategories.ToDisplayName(_category);

        public string DisplayName { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name!);
            return true;
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
