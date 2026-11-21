using RentalShared.Mqtt;

namespace RentalServer.Core.PcPolicy;

public interface IPcPolicyConfigPublisher
{
    Task PublishPcPolicyAsync(
        PcPolicyConfigMessage message,
        CancellationToken cancellationToken = default);
}
