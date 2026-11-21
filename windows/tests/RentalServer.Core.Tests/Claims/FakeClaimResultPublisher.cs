using RentalServer.Core.Claims;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Claims;

public sealed class FakeClaimResultPublisher : IClaimResultPublisher
{
    public List<(string StationId, ClaimResultMessage Message)> Published { get; } = new();

    public int PublishCount => Published.Count;

    public ClaimResultMessage? LastMessage => Published.Count == 0 ? null : Published[^1].Message;

    public Task PublishClaimResultAsync(
        string stationId,
        ClaimResultMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add((stationId, message));
        return Task.CompletedTask;
    }
}
