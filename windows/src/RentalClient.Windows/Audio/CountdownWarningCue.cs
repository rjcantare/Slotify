namespace RentalClient.Windows.Audio;

public enum CountdownWarningCueKind
{
    FirstWarning,
    FinalCountdown
}

public sealed record CountdownWarningCue(
    CountdownWarningCueKind Kind,
    long RemainingSec);
