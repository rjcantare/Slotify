using System.Globalization;
using System.Text.Json;
using RentalServer.Core.EventLog;
using RentalShared.Mqtt;

namespace RentalServer.Core.TabletAdmin;

public sealed class TabletAdminConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly TabletAdminRepository _repository;
    private readonly ITabletAdminConfigPublisher _publisher;
    private readonly IEventLogger? _eventLogger;

    public TabletAdminConfigService(
        TabletAdminRepository repository,
        ITabletAdminConfigPublisher publisher,
        IEventLogger? eventLogger = null)
    {
        _repository = repository;
        _publisher = publisher;
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
        await _publisher.PublishTabletAdminAsync(BuildMessage(version.Value, config, updatedAt), cancellationToken);
    }

    public async Task<int> UpdatePasswordAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);

        var publishedAt = FormatUtc(DateTimeOffset.UtcNow);
        var version = await _repository.PublishNewPasswordAsync(password, publishedAt, cancellationToken);
        var config = await _repository.GetCurrentConfigAsync(cancellationToken)
                     ?? throw new InvalidOperationException("Tablet admin config was not saved.");

        await _publisher.PublishTabletAdminAsync(BuildMessage(version, config, publishedAt), cancellationToken);
        await TryLogPublishedAsync(version, publishedAt, cancellationToken);
        return version;
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Admin password is required.", nameof(password));
        }

        if (password.Trim().Length < 6)
        {
            throw new ArgumentException("Admin password must be at least 6 characters.", nameof(password));
        }

        if (password.Trim().Length > 64)
        {
            throw new ArgumentException("Admin password must be 64 characters or fewer.", nameof(password));
        }
    }

    private async Task TryLogPublishedAsync(
        int version,
        string publishedAt,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { version, publishedAt }, JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.TabletAdminPasswordPublished,
                StationId: null,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    private static TabletAdminConfigMessage BuildMessage(
        int version,
        TabletAdminConfig config,
        string updatedAt)
    {
        return new TabletAdminConfigMessage
        {
            Version = version,
            UpdatedAt = updatedAt,
            Algorithm = config.Algorithm,
            PasswordSalt = config.PasswordSalt,
            PasswordHash = config.PasswordHash
        };
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
