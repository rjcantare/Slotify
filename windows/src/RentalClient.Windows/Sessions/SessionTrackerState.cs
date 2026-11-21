namespace RentalClient.Windows.Sessions;

public sealed record SessionTrackerState(
    bool HasSession,
    string SessionId,
    string StationId,
    string DeviceType,
    bool Locked,
    long EffectiveRemainingSec,
    long LastCreditSeqNo,
    string ServerUpdatedAt)
{
    public static SessionTrackerState NoSession { get; } =
        new(false, string.Empty, string.Empty, string.Empty, false, 0, 0, string.Empty);

    public string FormatRemaining()
    {
        var hours = EffectiveRemainingSec / 3600;
        var minutes = (EffectiveRemainingSec % 3600) / 60;
        var seconds = EffectiveRemainingSec % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }
}
