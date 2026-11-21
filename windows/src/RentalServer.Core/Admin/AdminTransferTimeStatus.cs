namespace RentalServer.Core.Admin;

public enum AdminTransferTimeStatus
{
    Applied,
    SameStation,
    SourceUnknown,
    TargetUnknown,
    SourceOffline,
    TargetOffline,
    NoSourceTime,
    TargetHasTime
}
