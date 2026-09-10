using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;

namespace FluentFlyout.App;

/// <summary>
/// Composition root for services that must outlive individual WinUI windows.
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
    private readonly KeyboardHookService keyboardHook = new();
    private readonly FullscreenService fullscreenService = new();
    private readonly DispatcherQueue uiQueue;
    private int disposed;
    private string lastTrackTitle = string.Empty;

    public AppRuntime(
        ISettingsStore settings,
        IMediaSessionService media,
        ITrayIconService tray,
        AppNotificationService notifications,
        DispatcherQueue uiQueue)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Media = media ?? throw new ArgumentNullException(nameof(media));
        Tray = tray ?? throw new ArgumentNullException(nameof(tray));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        this.uiQueue = uiQueue ?? throw new ArgumentNullException(nameof(uiQueue));

        Media.SnapshotChanged += OnMediaSnapshotChanged;
        SetupKeyboardHooks();
    }

    public ISettingsStore Settings { get; }

    public IMediaSessionService Media { get; }

    public ITrayIconService Tray { get; }

    public AppNotificationService Notifications { get; }

    public bool IsStarted { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (IsStarted)
            return;

        await Settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!Settings.Current.NIconHide)
            Tray.Show();
        Notifications.Register();

        keyboardHook.Start();

        await Media.RefreshAsync(cancellationToken).ConfigureAwait(true);
        IsStarted = true;
    }

    private void SetupKeyboardHooks()
    {
        keyboardHook.MediaKeyPressed += action =>
        {
            uiQueue.TryEnqueue(() =>
            {
                if (Settings.Current.DisableIfFullscreen && fullscreenService.IsFullscreenAppRunning())
                    return;

                if (Settings.Current.MediaFlyoutEnabled)
                {
                    App.Current.WindowManager.ShowMediaFlyout();
                }
            });
        };

        keyboardHook.VolumeKeyPressed += action =>
        {
            uiQueue.TryEnqueue(() =>
            {
                if (Settings.Current.DisableIfFullscreen && fullscreenService.IsFullscreenAppRunning())
                    return;

                if (Settings.Current.VolumeControlEnabled)
                {
                    App.Current.WindowManager.ShowVolumeMixer();
                }

                if (Settings.Current.MediaFlyoutEnabled && !Settings.Current.MediaFlyoutVolumeKeysExcluded)
                {
                    App.Current.WindowManager.ShowMediaFlyout();
                }
            });
        };

        keyboardHook.LockKeyPressed += (keyType, isEnabled) =>
        {
            uiQueue.TryEnqueue(() =>
            {
                if (Settings.Current.DisableIfFullscreen && fullscreenService.IsFullscreenAppRunning())
                    return;

                if (Settings.Current.LockKeysEnabled)
                {
                    string name = keyType switch
                    {
                        LockKeyType.CapsLock => "Caps Lock",
                        LockKeyType.NumLock => "Num Lock",
                        LockKeyType.ScrollLock => "Scroll Lock",
                        _ => "Lock Key"
                    };
                    App.Current.WindowManager.ShowLockKeys(name, isEnabled);
                }
            });
        };
    }

    private void OnMediaSnapshotChanged(object? sender, MediaSnapshotChangedEventArgs e)
    {
        uiQueue.TryEnqueue(() =>
        {
            // App filtering: blocklist/allowlist decide whether this session surfaces.
            if (!MediaFiltering.IsSessionAllowed(Settings.Current, e.Snapshot))
                return;

            App.Current.WindowManager.UpdateMedia(e.Snapshot);

            // NextUp banner: only on an actual track change while playing.
            if (Settings.Current.NextUpEnabled &&
                e.Snapshot.PlaybackState == MediaPlaybackState.Playing &&
                !string.IsNullOrWhiteSpace(e.Snapshot.Title) &&
                !string.Equals(e.Snapshot.Title, lastTrackTitle, StringComparison.Ordinal))
            {
                lastTrackTitle = e.Snapshot.Title;
                App.Current.WindowManager.ShowNextUp(e.Snapshot);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        keyboardHook.Dispose();

        if (Tray is IDisposable disposableTray)
            disposableTray.Dispose();
        Notifications.Dispose();
        if (Media is IAsyncDisposable asyncMedia)
            await asyncMedia.DisposeAsync().ConfigureAwait(false);
        else if (Media is IDisposable disposableMedia)
            disposableMedia.Dispose();
    }
}
