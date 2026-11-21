namespace RentalServer.Core.Rates;

public sealed record InsertCoinRateRule(
    int StarterMaxPesos,
    int StarterSeconds,
    int NormalizedFromPesos,
    int PerPesoSeconds)
{
    public int CalculateSeconds(int totalPesos)
    {
        if (totalPesos <= 0)
        {
            return 0;
        }

        if (totalPesos < StarterMaxPesos)
        {
            return 0;
        }

        if (totalPesos == StarterMaxPesos)
        {
            return StarterSeconds;
        }

        if (totalPesos < NormalizedFromPesos)
        {
            return StarterSeconds + ((totalPesos - StarterMaxPesos) * PerPesoSeconds);
        }

        return totalPesos * PerPesoSeconds;
    }
}
