using FluentFlyout.Core;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class MediaFilteringTests
{
    [Fact]
    public void NormalizeApplicationName_RemovesExeSuffix_AndWhitespace()
    {
        var normalized = MediaFiltering.NormalizeApplicationName("  Spotify.exe  ");

        Assert.Equal("Spotify", normalized);
    }

    [Fact]
    public void IsSessionAllowed_BlacklistMode_BlocksMatchingSession()
    {
        var settings = new UserSettings
        {
            AppFilteringEnabled = true,
            AppFilteringMode = 0,
            BlockedApps = ["spotify"],
        };

        var snapshot = new MediaSnapshot
        {
            SessionId = "Spotify.exe",
            AppId = "Spotify.exe",
            DisplayName = "Spotify",
        };

        Assert.False(MediaFiltering.IsSessionAllowed(settings, snapshot));
    }

    [Fact]
    public void IsSessionAllowed_WhitelistMode_OnlyAllowsMatchingSession()
    {
        var settings = new UserSettings
        {
            AppFilteringEnabled = true,
            AppFilteringMode = 1,
            AllowedApps = ["vlc"],
        };

        var allowedSnapshot = new MediaSnapshot
        {
            SessionId = "VideoLan.VLC",
            AppId = "VideoLan.VLC",
            DisplayName = "VLC media player",
        };

        var blockedSnapshot = new MediaSnapshot
        {
            SessionId = "Spotify.exe",
            AppId = "Spotify.exe",
            DisplayName = "Spotify",
        };

        Assert.True(MediaFiltering.IsSessionAllowed(settings, allowedSnapshot));
        Assert.False(MediaFiltering.IsSessionAllowed(settings, blockedSnapshot));
    }
}
