// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Utils;
using FluentFlyout.Windows;
using System.Windows;
using WindowsMediaController;

namespace FluentFlyout.Controls;

public partial class TaskbarWidgetControl
{
    private bool _openingPlayer;
    private MediaManager.MediaSession? _playerMenuSession;
    public bool IsPlayerMenuOpen => ContextMenu?.IsOpen == true;

    private void PlayerMenu_Opened(object sender, RoutedEventArgs e)
    {
        _playerMenuSession = _mainWindow?.GetTaskbarSession();
        OpenPlayerMenuItem.IsEnabled = !_openingPlayer && _playerMenuSession != null;
        (Window.GetWindow(this) as TaskbarWindow)?.RefreshOutsideClickHook();
    }

    private void PlayerMenu_Closed(object sender, RoutedEventArgs e)
    {
        _playerMenuSession = null;
        (Window.GetWindow(this) as TaskbarWindow)?.RefreshOutsideClickHook();
    }

    public void ClosePlayerMenu()
    {
        if (ContextMenu != null) ContextMenu.IsOpen = false;
    }

    private async void OpenPlayerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var session = _playerMenuSession;
        if (_openingPlayer || session == null) return;
        _openingPlayer = true;
        OpenPlayerMenuItem.IsEnabled = false;
        try
        {
            await Task.Run(async () =>
            {
                string? title = null;
                try { title = (await session.ControlSession.TryGetMediaPropertiesAsync()).Title; }
                catch { /* The window can still be activated without a media title. */ }
                return MediaPlayerData.TryActivateMediaPlayer(session.Id, title);
            });
        }
        catch (Exception ex) { Logger.Warn(ex, "Unable to open taskbar media player"); }
        finally { _openingPlayer = false; }
    }
}
