namespace RentalServer.Core.Claims;

public sealed record ClaimRecord(
    string ClaimId,
    string RequestId,
    string StationId,
    string AcceptorId,
    string GrantedAt,
    string ExpiresAt,
    string? ResolvedAt,
    ClaimStatus Status,
    ClaimRateMode RateMode = ClaimRateMode.InsertCoin);
