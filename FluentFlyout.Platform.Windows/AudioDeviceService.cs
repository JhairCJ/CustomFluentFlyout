using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FluentFlyout.Platform.Windows;

public sealed class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator deviceEnumerator;
    private MMDevice? defaultDevice;
    private bool disposed;

    public event Action<float>? MasterVolumeChanged;
    public event Action<bool>? MasterMuteChanged;
    public event Action? DefaultDeviceChanged;

    public AudioDeviceService()
    {
        deviceEnumerator = new MMDeviceEnumerator();
        RefreshDefaultDevice();
    }

    public float MasterVolume
    {
        get => defaultDevice?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0f;
        set
        {
            if (defaultDevice?.AudioEndpointVolume != null)
            {
                defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            }
        }
    }

    public bool IsMasterMuted
    {
        get => defaultDevice?.AudioEndpointVolume?.Mute ?? false;
        set
        {
            if (defaultDevice?.AudioEndpointVolume != null)
            {
                defaultDevice.AudioEndpointVolume.Mute = value;
            }
        }
    }

    public void ToggleMute()
    {
        IsMasterMuted = !IsMasterMuted;
    }

    public void RefreshDefaultDevice()
    {
        try
        {
            if (defaultDevice?.AudioEndpointVolume != null)
            {
                defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            }
            defaultDevice?.Dispose();
            defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice?.AudioEndpointVolume != null)
            {
                defaultDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            }
            DefaultDeviceChanged?.Invoke();
        }
        catch
        {
            defaultDevice = null;
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        MasterVolumeChanged?.Invoke(data.MasterVolume);
        MasterMuteChanged?.Invoke(data.Muted);
    }

    public MMDevice? GetCurrentDevice() => defaultDevice;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (defaultDevice?.AudioEndpointVolume != null)
        {
            defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
        }
        defaultDevice?.Dispose();
        deviceEnumerator.Dispose();
    }
}
