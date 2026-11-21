namespace RentalServer.Core.PcPolicy;

public sealed record PcPolicyConfig(
    bool AutoCloseOnLockEnabled,
    bool IdleShutdownEnabled,
    int IdleShutdownMinutes)
{
    public const int DefaultIdleShutdownMinutes = 5;

    public static PcPolicyConfig Default { get; } =
        new(
            AutoCloseOnLockEnabled: false,
            IdleShutdownEnabled: false,
            IdleShutdownMinutes: DefaultIdleShutdownMinutes);
}
