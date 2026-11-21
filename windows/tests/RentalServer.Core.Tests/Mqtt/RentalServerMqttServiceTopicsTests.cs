using RentalServer.Core.Mqtt;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Mqtt;

public sealed class RentalServerMqttServiceTopicsTests
{
    [Fact]
    public void SubscribedTopics_MatchesProtocolMatrix()
    {
        var expected = new[]
        {
            MqttTopics.StationStatusWildcard,
            MqttTopics.StationSessionClearWildcard,
            MqttTopics.StationClaimRequestWildcard,
            MqttTopics.StationClaimEndWildcard,
            MqttTopics.StationCreditAckWildcard,
            MqttTopics.StationAppInventoryWildcard,
            MqttTopics.CoinAcceptorStatusWildcard,
            MqttTopics.CoinAcceptorPulseWildcard,
            MqttTopics.StationCommandWildcard
        };

        Assert.Equal(expected, RentalServerMqttService.SubscribedTopics);
    }

    [Fact]
    public void SubscribedTopics_HasNoDuplicates()
    {
        var distinct = new HashSet<string>(RentalServerMqttService.SubscribedTopics);
        Assert.Equal(RentalServerMqttService.SubscribedTopics.Count, distinct.Count);
    }

    [Fact]
    public void SubscribedTopics_AllUseProtocolWildcardForm()
    {
        foreach (var topic in RentalServerMqttService.SubscribedTopics)
        {
            Assert.Contains("/+/", topic);
            Assert.StartsWith("rental/", topic);
        }
    }

    [Fact]
    public void CreditAckTopic_CanExtractStationId()
    {
        Assert.True(MqttTopics.TryGetStationIdFromCreditAckTopic(
            "rental/station/pc-01/credit/ack",
            out var stationId));
        Assert.Equal("pc-01", stationId);
    }
}
