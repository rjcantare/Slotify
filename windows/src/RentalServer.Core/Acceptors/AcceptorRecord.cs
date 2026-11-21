namespace RentalServer.Core.Acceptors;

public sealed record AcceptorRecord(
    string AcceptorId,
    string CreatedAt,
    string? LastSeenAt,
    string? FirmwareVersion,
    int? LastRssi,
    bool IsOnline,
    long LastProcessedPulseSeqNo,
    long? LastUptimeSec = null);
