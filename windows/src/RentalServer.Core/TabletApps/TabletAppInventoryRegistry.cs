using System.Text.Json;
using RentalShared.Mqtt;

namespace RentalServer.Core.TabletApps;

public sealed class TabletAppInventoryRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, TabletAppInventoryRecord> _byStation = new(StringComparer.Ordinal);

    public event EventHandler<IReadOnlyList<TabletAppInventoryRecord>>? InventoriesChanged;

    public IReadOnlyList<TabletAppInventoryRecord> GetAll()
    {
        lock (_gate)
        {
            return _byStation.Values
                .OrderBy(i => i.StationId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public Task HandleInventoryMessageAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!MqttTopics.TryGetStationIdFromAppInventoryTopic(topic, out var stationId))
        {
            return Task.CompletedTask;
        }

        TabletAppInventoryMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TabletAppInventoryMessage>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }

        if (message is null ||
            !string.Equals(message.StationId, stationId, StringComparison.Ordinal) ||
            !string.Equals(message.DeviceType, "tab", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var apps = message.Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName) &&
                        !string.IsNullOrWhiteSpace(a.PackageName))
            .GroupBy(a => a.PackageName.Trim(), StringComparer.Ordinal)
            .Select(g =>
            {
                var app = g.First();
                return new TabletInstalledAppRecord(
                    DisplayName: app.DisplayName.Trim(),
                    PackageName: app.PackageName.Trim(),
                    System: app.System);
            })
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.PackageName, StringComparer.Ordinal)
            .ToArray();

        lock (_gate)
        {
            _byStation[stationId] = new TabletAppInventoryRecord(
                StationId: stationId,
                DeviceType: message.DeviceType,
                PublishedAt: message.PublishedAt,
                Apps: apps);
        }

        InventoriesChanged?.Invoke(this, GetAll());
        return Task.CompletedTask;
    }
}
