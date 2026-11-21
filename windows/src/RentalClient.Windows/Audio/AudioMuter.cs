using NAudio.CoreAudioApi;

namespace RentalClient.Windows.Audio;

// Mutes the system master volume on lock, restores the prior state on unlock.
//
// Respects the user's prior mute setting: if they had muted before the lock,
// unlock leaves it muted. We never unmute audio the user didn't have on.
//
// Caches a reference to the default render endpoint at construction. If the
// default audio device changes mid-session (USB DAC unplugged, Windows
// default switched), Mute()/Unmute() will target the old device. Acceptable
// for v1 — kiosk PCs don't typically hot-swap audio hardware. Revisit if
// production reports show this is an issue.
public sealed class AudioMuter : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _isMutedByUs;
    private bool _originalMute;

    public AudioMuter()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            // No render device available (audio service disabled, headless box, etc.).
            // Mute()/Unmute() become no-ops via the null check below.
        }
    }

    public void Mute()
    {
        if (_device is null || _isMutedByUs) return;

        try
        {
            _originalMute = _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.Mute = true;
            _isMutedByUs = true;
        }
        catch
        {
            // Device disappeared between construction and now — silently degrade.
        }
    }

    public void Unmute()
    {
        if (_device is null || !_isMutedByUs) return;

        try
        {
            _device.AudioEndpointVolume.Mute = _originalMute;
        }
        catch
        {
            // Device disappeared; nothing useful to do.
        }
        finally
        {
            _isMutedByUs = false;
        }
    }

    public void Dispose()
    {
        Unmute();
        _device?.Dispose();
        _enumerator?.Dispose();
        _device = null;
        _enumerator = null;
    }
}
