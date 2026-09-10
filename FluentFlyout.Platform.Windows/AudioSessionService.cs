using System.Diagnostics;
using FluentFlyout.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FluentFlyout.Platform.Windows;

public sealed class AudioSessionService
{
    private readonly AudioDeviceService audioDeviceService;

    public AudioSessionService(AudioDeviceService audioDeviceService)
    {
        this.audioDeviceService = audioDeviceService ?? throw new ArgumentNullException(nameof(audioDeviceService));
    }

    public IReadOnlyList<AudioSessionItem> GetActiveSessions()
    {
        var result = new List<AudioSessionItem>();
        var device = audioDeviceService.GetCurrentDevice();
        if (device == null) return result;

        try
        {
            var sessionManager = device.AudioSessionManager;
            if (sessionManager == null) return result;

            var sessions = sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null) continue;

                int pid = (int)session.GetProcessID;
                if (pid <= 0) continue; // System sounds or invalid process

                string name = string.Empty;
                string? exePath = null;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    name = proc.ProcessName;
                    exePath = proc.MainModule?.FileName;
                }
                catch
                {
                    name = $"Process {pid}";
                }

                if (!string.IsNullOrWhiteSpace(session.DisplayName))
                {
                    name = session.DisplayName;
                }

                result.Add(new AudioSessionItem
                {
                    ProcessId = pid,
                    DisplayName = name,
                    Volume = session.SimpleAudioVolume.Volume,
                    IsMuted = session.SimpleAudioVolume.Mute,
                    IsActive = session.State == AudioSessionState.AudioSessionStateActive,
                    ExecutablePath = exePath
                });
            }
        }
        catch
        {
            // Session query failure
        }

        return result;
    }

    public void SetSessionVolume(int processId, float volume)
    {
        var device = audioDeviceService.GetCurrentDevice();
        if (device?.AudioSessionManager == null) return;

        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session != null && session.GetProcessID == processId)
                {
                    session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                    break;
                }
            }
        }
        catch { }
    }

    public void SetSessionMute(int processId, bool isMuted)
    {
        var device = audioDeviceService.GetCurrentDevice();
        if (device?.AudioSessionManager == null) return;

        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session != null && session.GetProcessID == processId)
                {
                    session.SimpleAudioVolume.Mute = isMuted;
                    break;
                }
            }
        }
        catch { }
    }
}
