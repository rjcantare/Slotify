using RentalServer.Core.PcPolicy;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.PcPolicy;

public sealed class FakePcPolicyConfigPublisher : IPcPolicyConfigPublisher
{
    public List<PcPolicyConfigMessage> Published { get; } = new();

    public PcPolicyConfigMessage? LastMessage =>
        Published.Count == 0 ? null : Published[^1];

    public Task PublishPcPolicyAsync(
        PcPolicyConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
