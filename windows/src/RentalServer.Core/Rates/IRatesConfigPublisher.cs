using RentalShared.Mqtt;

namespace RentalServer.Core.Rates;

// Implemented by RentalServerMqttService. Publishes a RateConfigMessage to
// `rental/config/rates` with retain=true so freshly-connected stations always
// see the latest rate sheet on subscribe.
public interface IRatesConfigPublisher
{
    Task PublishRatesAsync(RateConfigMessage message, CancellationToken cancellationToken = default);
}
