using RentalServer.Core.Sessions;

namespace RentalServer.Core.Admin;

public sealed record AdminRemoveTimeResult(
    AdminRemoveTimeStatus Status,
    string StationId,
    int RequestedSecondsToRemove,
    int ActualRemovedSec,
    bool NowLocked,
    SessionState? Session)
{
    public bool Applied => Status == AdminRemoveTimeStatus.Applied;
}
