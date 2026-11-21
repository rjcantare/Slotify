using RentalServer.Core.Rates;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Rates;

public sealed class FakeRatesConfigPublisher : IRatesConfigPublisher
{
    public List<RateConfigMessage> Published { get; } = new();

    public RateConfigMessage? LastMessage =>
        Published.Count == 0 ? null : Published[^1];

    public int PublishCount => Published.Count;

    public Task PublishRatesAsync(
        RateConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
