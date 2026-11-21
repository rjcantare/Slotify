using RentalClient.Windows.Configuration;
using RentalClient.Windows.Sessions;

namespace RentalClient.Windows.Audio;

public sealed class CountdownWarningService
{
    private readonly CountdownWarningConfig _config;
    private readonly ICountdownWarningSoundPlayer _soundPlayer;
    private readonly HashSet<long> _finalCountdownSecondsPlayed = new();

    private string? _sessionId;
    private bool _firstWarningPlayed;

    public CountdownWarningService(
        CountdownWarningConfig config,
        ICountdownWarningSoundPlayer soundPlayer)
    {
        _config = config;
        _soundPlayer = soundPlayer;
    }

    public CountdownWarningCue? Update(SessionTrackerState state)
    {
        var cue = GetCue(state);
        if (cue is not null)
        {
            _soundPlayer.Play(cue);
        }

        return cue;
    }

    private CountdownWarningCue? GetCue(SessionTrackerState state)
    {
        if (!_config.Enabled || !state.HasSession || state.Locked || state.EffectiveRemainingSec <= 0)
        {
            Reset();
            return null;
        }

        if (_sessionId != state.SessionId)
        {
            Reset();
            _sessionId = state.SessionId;
        }

        var remaining = state.EffectiveRemainingSec;

        if (remaining > _config.FirstWarningSec)
        {
            _firstWarningPlayed = false;
        }

        if (remaining > _config.FinalCountdownStartSec)
        {
            _finalCountdownSecondsPlayed.Clear();
        }

        if (remaining <= _config.FinalCountdownStartSec)
        {
            return _finalCountdownSecondsPlayed.Add(remaining)
                ? new CountdownWarningCue(CountdownWarningCueKind.FinalCountdown, remaining)
                : null;
        }

        if (remaining <= _config.FirstWarningSec && !_firstWarningPlayed)
        {
            _firstWarningPlayed = true;
            return new CountdownWarningCue(CountdownWarningCueKind.FirstWarning, remaining);
        }

        return null;
    }

    private void Reset()
    {
        _sessionId = null;
        _firstWarningPlayed = false;
        _finalCountdownSecondsPlayed.Clear();
    }
}
