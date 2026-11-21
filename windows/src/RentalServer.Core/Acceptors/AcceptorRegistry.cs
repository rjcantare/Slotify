using System.Globalization;
using System.Text.Json;
using RentalShared.Mqtt;

namespace RentalServer.Core.Acceptors;

public sealed class AcceptorRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AcceptorRepository _repository;

    public AcceptorRegistry(AcceptorRepository repository)
    {
        _repository = repository;
    }

    public event EventHandler<IReadOnlyList<AcceptorRecord>>? AcceptorsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken);
        await PublishSnapshotAsync(cancellationToken);
    }

    public async Task HandleStatusMessageAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!MqttTopics.TryGetAcceptorIdFromStatusTopic(topic, out var acceptorId))
        {
            return;
        }

        var message = JsonSerializer.Deserialize<AcceptorStatusMessage>(payload, JsonOptions);
        if (message is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.DeviceId) &&
            !string.Equals(message.DeviceId, acceptorId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var receivedAt = FormatUtc(DateTimeOffset.UtcNow);

        if (message.Online)
        {
            await _repository.UpsertOnlineAsync(
                acceptorId,
                message.FirmwareVersion,
                message.Rssi,
                receivedAt,
                uptimeSec: message.UptimeSec,
                cancellationToken: cancellationToken);
        }
        else
        {
            await _repository.MarkOfflineAsync(acceptorId, receivedAt, cancellationToken);
        }

        await PublishSnapshotAsync(cancellationToken);
    }

    public Task<AcceptorRecord?> GetAsync(string acceptorId, CancellationToken cancellationToken = default)
    {
        return _repository.GetAsync(acceptorId, cancellationToken);
    }

    private async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        AcceptorsChanged?.Invoke(this, await _repository.GetAllAsync(cancellationToken));
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
