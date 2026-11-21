using System.Globalization;
using System.Text.Json;
using RentalServer.Core.EventLog;
using RentalShared.Mqtt;

namespace RentalServer.Core.PcPolicy;

public sealed class PcPolicyConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly PcPolicyRepository _repository;
    private readonly IPcPolicyConfigPublisher _publisher;
    private readonly IEventLogger? _eventLogger;

    public PcPolicyConfigService(
        PcPolicyRepository repository,
        IPcPolicyConfigPublisher publisher,
        IEventLogger? eventLogger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _eventLogger = eventLogger;
    }

    public async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        var version = await _repository.GetCurrentVersionAsync(cancellationToken);
        var config = await _repository.GetCurrentConfigAsync(cancellationToken);
        if (version is null || config is null)
        {
            return;
        }

        var updatedAt = await _repository.GetCurrentPublishedAtAsync(cancellationToken)
                        ?? FormatUtc(DateTimeOffset.UtcNow);
        await _publisher.PublishPcPolicyAsync(BuildMessage(version.Value, config, updatedAt), cancellationToken);
    }

    public async Task<(int Version, PcPolicyConfig Config)> UpdateConfigAsync(
        PcPolicyConfig config,
        CancellationToken cancellationToken = default)
    {
        ValidateConfig(config);

        var publishedAt = FormatUtc(DateTimeOffset.UtcNow);
        var version = await _repository.PublishNewVersionAsync(config, publishedAt, cancellationToken);
        await _publisher.PublishPcPolicyAsync(BuildMessage(version, config, publishedAt), cancellationToken);
        await TryLogPublishedAsync(version, config, publishedAt, cancellationToken);
        return (version, config);
    }

    public static void ValidateConfig(PcPolicyConfig config)
    {
        if (config.IdleShutdownMinutes < 1 || config.IdleShutdownMinutes > 1440)
        {
            throw new ArgumentException("Idle shutdown minutes must be between 1 and 1440.", nameof(config));
        }
    }

    public static PcPolicyConfigMessage BuildMessage(
        int version,
        PcPolicyConfig config,
        string updatedAt)
    {
        return new PcPolicyConfigMessage
        {
            Version = version,
            UpdatedAt = updatedAt,
            AutoCloseOnLock = new PcAutoCloseOnLockPolicyMessage
            {
                Enabled = config.AutoCloseOnLockEnabled
            },
            IdleShutdown = new PcIdleShutdownPolicyMessage
            {
                Enabled = config.IdleShutdownEnabled,
                IdleMinutes = config.IdleShutdownMinutes
            }
        };
    }

    private async Task TryLogPublishedAsync(
        int version,
        PcPolicyConfig config,
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
                autoCloseOnLockEnabled = config.AutoCloseOnLockEnabled,
                idleShutdownEnabled = config.IdleShutdownEnabled,
                idleShutdownMinutes = config.IdleShutdownMinutes
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.PcPolicyPublished,
                StationId: null,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
