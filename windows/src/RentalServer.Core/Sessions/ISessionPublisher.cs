namespace RentalServer.Core.Sessions;

// Abstraction over the retained `rental/station/{stationId}/session` MQTT publish.
// SessionService depends on this (not on the concrete MQTT service) so the timer
// state machine can be unit-tested without a real broker.
public interface ISessionPublisher
{
    Task PublishSessionAsync(SessionState state, CancellationToken cancellationToken = default);
}
