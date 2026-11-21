using RentalServer.Core.Sessions;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Sessions;

public sealed class FakeCreditPublisher : ICreditPublisher
{
    public List<(string StationId, CreditMessage Message)> Published { get; } = new();

    public int PublishCount => Published.Count;

    public CreditMessage? LastMessage => Published.Count == 0 ? null : Published[^1].Message;

    public Task PublishCreditAsync(
        string stationId,
        CreditMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add((stationId, message));
        return Task.CompletedTask;
    }
}
