namespace RentalShared.Mqtt;

public static class MqttTopics
{
    public const string DevHello = "rental/_dev/hello";

    // Retained rate sheet — server publishes on startup and after every admin edit.
    public const string ConfigRates = "rental/config/rates";
    public const string ConfigTabletApps = "rental/config/tablet-apps";
    public const string ConfigTabletAdmin = "rental/config/tablet-admin";
    public const string ConfigAbusePrevention = "rental/config/abuse-prevention";
    public const string ConfigPcPolicy = "rental/config/pc-policy";

    // Wildcards that the server subscribes to.
    public const string StationStatusWildcard = "rental/station/+/status";
    public const string StationClaimRequestWildcard = "rental/station/+/claim/request";
    public const string StationClaimEndWildcard = "rental/station/+/claim/end";
    public const string StationSessionClearWildcard = "rental/station/+/session/clear";
    public const string StationCreditAckWildcard = "rental/station/+/credit/ack";
    public const string StationAppInventoryWildcard = "rental/station/+/apps";
    public const string CoinAcceptorStatusWildcard = "rental/coinacceptor/+/status";
    public const string CoinAcceptorPulseWildcard = "rental/coinacceptor/+/pulse";
    public const string StationCommandWildcard = "rental/station/+/command/ack";

    public static string StationStatus(string stationId) =>
        $"rental/station/{stationId}/status";

    public static string StationSession(string stationId) =>
        $"rental/station/{stationId}/session";

    public static string StationClaimRequest(string stationId) =>
        $"rental/station/{stationId}/claim/request";

    public static string StationClaimResult(string stationId) =>
        $"rental/station/{stationId}/claim/result";

    public static string StationClaimEnd(string stationId) =>
        $"rental/station/{stationId}/claim/end";

    public static string StationSessionClear(string stationId) =>
        $"rental/station/{stationId}/session/clear";

    public static string StationAdminOpen(string stationId) =>
        $"rental/station/{stationId}/admin/open";

    public static string StationCredit(string stationId) =>
        $"rental/station/{stationId}/credit";

    public static string StationCreditAck(string stationId) =>
        $"rental/station/{stationId}/credit/ack";

    public static string StationAppInventory(string stationId) =>
        $"rental/station/{stationId}/apps";

    public static string StationCommand(string stationId) =>
        $"rental/station/{stationId}/command";

    public static string StationCommandAck(string stationId) =>
        $"rental/station/{stationId}/command/ack";

    public static string CoinAcceptorStatus(string acceptorId) =>
        $"rental/coinacceptor/{acceptorId}/status";

    public static string CoinAcceptorPulse(string acceptorId) =>
        $"rental/coinacceptor/{acceptorId}/pulse";

    public static string CoinAcceptorPulseAck(string acceptorId) =>
        $"rental/coinacceptor/{acceptorId}/pulse/ack";

    public static string CoinAcceptorLed(string acceptorId) =>
        $"rental/coinacceptor/{acceptorId}/led";

    public static bool TryGetStationIdFromStatusTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/status", out stationId);

    public static bool TryGetStationIdFromClaimRequestTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/claim/request", out stationId);

    public static bool TryGetStationIdFromClaimEndTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/claim/end", out stationId);

    public static bool TryGetStationIdFromSessionClearTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/session/clear", out stationId);

    public static bool TryGetStationIdFromCreditAckTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/credit/ack", out stationId);

    public static bool TryGetStationIdFromAppInventoryTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/apps", out stationId);

    public static bool TryGetStationIdFromCommandAckTopic(string topic, out string stationId) =>
        TryExtractIdBetween(topic, "rental/station/", "/command/ack", out stationId);

    public static bool TryGetAcceptorIdFromStatusTopic(string topic, out string acceptorId) =>
        TryExtractIdBetween(topic, "rental/coinacceptor/", "/status", out acceptorId);

    public static bool TryGetAcceptorIdFromPulseTopic(string topic, out string acceptorId) =>
        TryExtractIdBetween(topic, "rental/coinacceptor/", "/pulse", out acceptorId);

    private static bool TryExtractIdBetween(string topic, string prefix, string suffix, out string id)
    {
        id = string.Empty;

        if (!topic.StartsWith(prefix, StringComparison.Ordinal) ||
            !topic.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var idLength = topic.Length - prefix.Length - suffix.Length;
        if (idLength <= 0)
        {
            return false;
        }

        id = topic.Substring(prefix.Length, idLength);
        return id.Length > 0 && !id.Contains('/', StringComparison.Ordinal);
    }
}
