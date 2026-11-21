using System.Globalization;
using System.Text.Json;
using RentalShared.Mqtt;

namespace RentalServer.Core.Stations;

public sealed class StationRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly StationRepository _repository;

    public StationRegistry(StationRepository repository)
    {
        _repository = repository;
    }

    public event EventHandler<IReadOnlyList<StationRecord>>? StationsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken);
        await PublishSnapshotAsync(cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return PublishSnapshotAsync(cancellationToken);
    }

    public async Task HandleStatusMessageAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!MqttTopics.TryGetStationIdFromStatusTopic(topic, out var stationId))
        {
            return;
        }

        var message = JsonSerializer.Deserialize<StationStatusMessage>(payload, JsonOptions);
        if (message is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.DeviceId) &&
            !string.Equals(message.DeviceId, stationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var receivedAt = FormatUtc(DateTimeOffset.UtcNow);

        if (message.Online)
        {
            if (!IsValidDeviceType(message.DeviceType))
            {
                return;
            }

            await _repository.UpsertOnlineAsync(
                stationId,
                message.DeviceType!,
                message.AppVersion,
                receivedAt,
                cancellationToken);
        }
        else
        {
            await _repository.MarkOfflineAsync(stationId, receivedAt, cancellationToken);
        }

        await PublishSnapshotAsync(cancellationToken);
    }

    private async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        StationsChanged?.Invoke(this, await _repository.GetAllAsync(cancellationToken));
    }

    private static bool IsValidDeviceType(string? deviceType)
    {
        return deviceType is "pc" or "phn" or "tab";
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
