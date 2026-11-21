using RentalServer.Core.Sessions;

namespace RentalServer.Core.Admin;

public sealed record AdminAddTimeResult(
    AdminAddTimeStatus Status,
    string StationId,
    int SecondsToAdd,
    SessionState? Session)
{
    public bool Applied => Status == AdminAddTimeStatus.Applied;
}
