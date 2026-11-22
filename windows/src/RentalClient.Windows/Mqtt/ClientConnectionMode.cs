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
    bool CanRequestCoinSlot,
    // Short owner-facing hint shown under "Please wait for connection." once
    // the PC has been offline for a while. Null = show nothing extra.
    string? OwnerHint = null);
