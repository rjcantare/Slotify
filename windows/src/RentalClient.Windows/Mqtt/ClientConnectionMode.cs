namespace RentalClient.Windows.Mqtt;

public enum ClientConnectionMode
{
    OnlineAuthoritative,
    OfflineAutonomous,
    OfflineUntrusted
}

public sealed record ClientConnectionState(
    ClientConnectionMode Mode,
    string Label,
    string Detail,
    bool CanRequestCoinSlot);
