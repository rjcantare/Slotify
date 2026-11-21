namespace RentalServer.Core.Claims;

public enum ClaimRateMode
{
    InsertCoin,
    AddTime
}

public static class ClaimRateModeExtensions
{
    public static string ToDbValue(this ClaimRateMode mode) => mode switch
    {
        ClaimRateMode.InsertCoin => "insert_coin",
        ClaimRateMode.AddTime => "add_time",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown claim rate mode.")
    };

    public static ClaimRateMode FromDbValue(string value) => value switch
    {
        "insert_coin" => ClaimRateMode.InsertCoin,
        "add_time" => ClaimRateMode.AddTime,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown claim rate mode string.")
    };
}
