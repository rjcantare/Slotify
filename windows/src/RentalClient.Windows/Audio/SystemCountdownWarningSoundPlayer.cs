using System.Media;

namespace RentalClient.Windows.Audio;

public sealed class SystemCountdownWarningSoundPlayer : ICountdownWarningSoundPlayer
{
    public void Play(CountdownWarningCue cue)
    {
        try
        {
            if (cue.Kind == CountdownWarningCueKind.FirstWarning)
            {
                SystemSounds.Asterisk.Play();
                return;
            }

            var sound = cue.RemainingSec <= 3
                ? SystemSounds.Hand
                : SystemSounds.Exclamation;
            sound.Play();
        }
        catch
        {
            // Audio device failures should never break kiosk timing or locking.
        }
    }
}
