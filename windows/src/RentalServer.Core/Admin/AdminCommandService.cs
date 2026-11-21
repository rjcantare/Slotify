using System.Globalization;
using System.Text.Json;
using RentalServer.Core.EventLog;
using RentalServer.Core.Stations;
using RentalShared.Mqtt;

namespace RentalServer.Core.Admin;

/// <summary>
/// S49: server-side dispatch for the admin restart/reboot/shutdown commands.
/// Validates the target station, mints a requestId, writes the audit row,
/// then publishes via <see cref="IAdminCommandPublisher"/>. The publish row
/// is the canonical record of operator intent; client acks are best-effort
/// because reboot/shutdown race their own ack against OS teardown.
/// </summary>
public sealed class AdminCommandService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly StationRepository _stationRepository;
    private readonly IAdminCommandPublisher _publisher;
    private readonly IEventLogger? _eventLogger;

    public AdminCommandService(
        StationRepository stationRepository,
        IAdminCommandPublisher publisher,
        IEventLogger? eventLogger = null)
    {
        _stationRepository = stationRepository ?? throw new ArgumentNullException(nameof(stationRepository));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _eventLogger = eventLogger;
    }

    public Task<AdminCommandResult> RestartClientAsync(string stationId, string reason, CancellationToken cancellationToken = default) =>
        DispatchAsync(stationId, "restart_client", EventTypes.AdminRestartClient, reason, cancellationToken);

    public Task<AdminCommandResult> RebootPcAsync(string stationId, string reason, CancellationToken cancellationToken = default) =>
        DispatchAsync(stationId, "reboot_pc", EventTypes.AdminRebootPc, reason, cancellationToken);

    public Task<AdminCommandResult> ShutdownPcAsync(string stationId, string reason, CancellationToken cancellationToken = default) =>
        DispatchAsync(stationId, "shutdown_pc", EventTypes.AdminShutdownPc, reason, cancellationToken);

    private async Task<AdminCommandResult> DispatchAsync(
        string stationId,
        string command,
        string eventType,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            throw new ArgumentException("Station id is required.", nameof(stationId));
        }

        var station = await _stationRepository.GetAsync(stationId, cancellationToken);
        if (station is null)
        {
            return new AdminCommandResult(AdminCommandStatus.StationUnknown, stationId, command, RequestId: null);
        }

        if (!station.IsOnline)
        {
            return new AdminCommandResult(AdminCommandStatus.StationOffline, stationId, command, RequestId: null);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var requestId = $"cmd-{nowUtc.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..36];
        var issuedAt = FormatUtc(nowUtc);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "operator" : reason.Trim();

        var message = new AdminCommandMessage
        {
            RequestId = requestId,
            Command = command,
            IssuedAt = issuedAt,
            StationId = stationId,
            Reason = normalizedReason
        };

        // Audit row first so the operator's intent is recorded even if the
        // publish itself errors. The publish landing the broker is a separate
        // concern from "we asked for X" — that question is settled here.
        if (_eventLogger is not null)
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    requestId,
                    command,
                    reason = normalizedReason,
                    deviceType = station.DeviceType
                },
                JsonOptions);

            await _eventLogger.AppendAsync(
                new EventLogAppendRequest(
                    Ts: issuedAt,
                    EventType: eventType,
                    StationId: stationId,
                    AcceptorId: null,
                    AmountPesos: null,
                    SecondsCredited: null,
                    Payload: payload),
                cancellationToken);
        }

        await _publisher.PublishCommandAsync(message, cancellationToken);

        return new AdminCommandResult(AdminCommandStatus.Published, stationId, command, requestId);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }
}

public enum AdminCommandStatus
{
    Published,
    StationUnknown,
    StationOffline
}

public sealed record AdminCommandResult(
    AdminCommandStatus Status,
    string StationId,
    string Command,
    string? RequestId);
