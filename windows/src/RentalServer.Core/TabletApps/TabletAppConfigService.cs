using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RentalServer.Core.EventLog;
using RentalShared.Mqtt;

namespace RentalServer.Core.TabletApps;

public sealed partial class TabletAppConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly TabletAppRepository _repository;
    private readonly ITabletAppConfigPublisher _publisher;
    private readonly IEventLogger? _eventLogger;

    public TabletAppConfigService(
        TabletAppRepository repository,
        ITabletAppConfigPublisher publisher,
        IEventLogger? eventLogger = null)
    {
        _repository = repository;
        _publisher = publisher;
        _eventLogger = eventLogger;
    }

    public Task<TabletAppConfig?> GetCurrentConfigAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetCurrentConfigAsync(cancellationToken);
    }

    public async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        var version = await _repository.GetCurrentVersionAsync(cancellationToken);
        if (version is null)
        {
            return;
        }

        var config = await _repository.GetCurrentConfigAsync(cancellationToken);
        if (config is null)
        {
            return;
        }

        var updatedAt = await _repository.GetCurrentPublishedAtAsync(cancellationToken)
                        ?? FormatUtc(DateTimeOffset.UtcNow);
        await _publisher.PublishTabletAppsAsync(BuildMessage(version.Value, config, updatedAt), cancellationToken);
    }

    public async Task<int> UpdateConfigAsync(
        TabletAppConfig config,
        CancellationToken cancellationToken = default)
    {
        ValidateConfig(config);

        var publishedAt = FormatUtc(DateTimeOffset.UtcNow);
        var version = await _repository.PublishNewVersionAsync(config, publishedAt, cancellationToken);
        await _publisher.PublishTabletAppsAsync(BuildMessage(version, config, publishedAt), cancellationToken);
        await TryLogPublishedAsync(version, config, publishedAt, cancellationToken);
        return version;
    }

    public static void ValidateConfig(TabletAppConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (config.Apps.Count == 0)
        {
            throw new ArgumentException("Add at least one tablet app.", nameof(config));
        }

        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in config.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.DisplayName))
            {
                throw new ArgumentException("Tablet app display name is required.", nameof(config));
            }

            if (string.IsNullOrWhiteSpace(app.PackageName))
            {
                throw new ArgumentException($"{app.DisplayName}: package name is required.", nameof(config));
            }

            if (!PackageNameRegex().IsMatch(app.PackageName))
            {
                throw new ArgumentException($"{app.DisplayName}: invalid Android package name '{app.PackageName}'.", nameof(config));
            }

            if (!packages.Add(app.PackageName))
            {
                throw new ArgumentException($"Duplicate tablet app package '{app.PackageName}'.", nameof(config));
            }

            if (app.SortOrder < 0)
            {
                throw new ArgumentException($"{app.DisplayName}: sort order must be >= 0.", nameof(config));
            }

            if (!TabletAppCategories.IsKnown(app.Category))
            {
                throw new ArgumentException($"{app.DisplayName}: invalid app category '{app.Category}'.", nameof(config));
            }
        }
    }

    private async Task TryLogPublishedAsync(
        int version,
        TabletAppConfig config,
        string publishedAt,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                version,
                publishedAt,
                apps = config.Apps
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.TabletAppsPublished,
                StationId: null,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    private static TabletAppConfigMessage BuildMessage(
        int version,
        TabletAppConfig config,
        string updatedAt)
    {
        return new TabletAppConfigMessage
        {
            Version = version,
            UpdatedAt = updatedAt,
            Apps = config.Apps
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.DisplayName, StringComparer.Ordinal)
                .Select(a => new TabletAppConfigItemMessage
                {
                    DisplayName = a.DisplayName,
                    PackageName = a.PackageName,
                    Enabled = a.Enabled,
                    DeepFreeze = a.DeepFreeze,
                    SortOrder = a.SortOrder,
                    Category = TabletAppCategories.Normalize(a.Category)
                })
                .ToArray()
        };
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")]
    private static partial Regex PackageNameRegex();

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
