namespace RentalServer.Core.Claims;

// Values match the schema's CHECK constraint exactly — do not rename without a
// migration. ToString() of these enum values is what gets written to SQLite.
public enum ClaimStatus
{
    Pending,
    Fulfilled,
    Expired,
    Cancelled
}

public static class ClaimStatusExtensions
{
    public static string ToDbValue(this ClaimStatus status) => status switch
    {
        ClaimStatus.Pending => "pending",
        ClaimStatus.Fulfilled => "fulfilled",
        ClaimStatus.Expired => "expired",
        ClaimStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown claim status.")
    };

    public static ClaimStatus FromDbValue(string value) => value switch
    {
        "pending" => ClaimStatus.Pending,
        "fulfilled" => ClaimStatus.Fulfilled,
        "expired" => ClaimStatus.Expired,
        "cancelled" => ClaimStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown claim status string.")
    };
}
