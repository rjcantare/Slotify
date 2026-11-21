using RentalServer.Core.Sessions;

namespace RentalServer.Core.Admin;

public sealed record AdminTransferTimeResult(
    AdminTransferTimeStatus Status,
    string SourceStationId,
    string TargetStationId,
    int SecondsTransferred,
    SessionState? SourceSession,
    SessionState? TargetSession)
{
    public bool Applied => Status == AdminTransferTimeStatus.Applied;
}
