// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using NAudio.CoreAudioApi;

namespace FluentFlyoutWPF.Classes;

public class AudioDeviceMonitor : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static AudioDeviceMonitor? _instance;
    private static readonly object _instanceLock = new();

    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDeviceNotificationClient? _notificationClient;

    public event EventHandler<DefaultDeviceChangedEventArgs>? DefaultDeviceChanged;

    public static AudioDeviceMonitor Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new AudioDeviceMonitor();
                }
            }
            return _instance;
        }
    }

    private AudioDeviceMonitor()
    {
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            // NAudio 3: event-based notifications (the raw IMMNotificationClient
            // COM interface is internal now). Events arrive on the captured
            // SynchronizationContext, so handlers can touch UI state directly.
            _notificationClient = _deviceEnumerator.CreateNotificationClient();
            _notificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;

            Logger.Info("Audio device monitoring initialized");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize audio device monitoring");
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        // Render devices are output devices, no e.Role check because roles are quite often randomly assigned
        if (e.Flow != DataFlow.Render)
            return;

        Logger.Info("Default audio output device changed");
        DefaultDeviceChanged?.Invoke(this, e);
    }

    public MMDevice? GetDefaultRenderDevice()
    {
        try
        {
            var device = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Logger.Debug("New device: {0}", device);
            return device;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get default render device");
            return null;
        }
    }

    public MMDevice? GetDeviceById(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;
        try
        {
            var device = _deviceEnumerator?.GetDevice(deviceId);
            Logger.Debug("Got device by id {0}: {1}", deviceId, device);
            return device;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get device by id {0}", deviceId);
            // TODO: we could investigate if returning GetDefaultRenderDevice() works as a fallback for returning null
            return null;
        }
    }

    public void Dispose()
    {
        if (_notificationClient != null)
        {
            try
            {
                _notificationClient.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                // Disposing unregisters from endpoint notifications.
                _notificationClient.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to dispose device notification client");
            }
            finally
            {
                _notificationClient = null;
            }
        }

        if (_deviceEnumerator != null)
        {
            try
            {
                _deviceEnumerator.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to dispose device enumerator");
            }
            finally
            {
                _deviceEnumerator = null;
            }
        }

        GC.SuppressFinalize(this);
    }
}