using System.Xml.Serialization;

namespace FluentFlyout.Core;

[XmlRoot("UserSettings")]
public sealed class UserSettings
{
    public const int CurrentSchemaVersion = 2;

    public UserSettings()
    {
        AppFilteringEnabled = false;
        AppFilteringMode = 0;
        AllowedApps = [];
        BlockedApps = [];
        Startup = true;
        MediaFlyoutEnabled = true;
        TaskbarWidgetEnabled = false;
        AppLanguage = "system";
        SchemaVersion = CurrentSchemaVersion;
        FontFamily = "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI";
        TaskbarWidgetFontFamily = "Segoe UI Variable";
        LastKnownVersion = string.Empty;
        Duration = 3000;
        NextUpDuration = 2000;
        LockKeysDuration = 2000;
        VolumeControlDuration = 3000;
        TaskbarWidgetBorderRadius = 6;
        TaskbarWidgetAlbumArtRadius = 5;
        TaskbarWidgetButtonHoverRadius = 6;
        TaskbarWidgetFixedWidthPx = 216;
        TaskbarWidgetTitleFontSize = 13;
        TaskbarWidgetArtistFontSize = 12;
        TaskbarWidgetScrollingTextSpeed = 20;
        TaskbarWidgetBackgroundBlurIntensity = 65;
        TaskbarWidgetBackgroundBlurRadius = 35;
        TaskbarWidgetBackgroundRotateDuration = 20;
        TaskbarWidgetBackgroundRotateSize = 300;
        TaskbarVisualizerBarCount = 10;
        TaskbarVisualizerAudioSensitivity = 2;
        TaskbarVisualizerAudioPeakLevel = 3;
        TaskbarVisualizerSmoothing = 50;
        AcrylicBlurOpacity = 175;
        AlbumAccentDesaturationThreshold = 65;
    }

    [XmlElement]
    public int SchemaVersion { get; set; }

    [XmlElement]
    public bool AppFilteringEnabled { get; set; }

    /// <summary>
    /// 0 = blacklist, 1 = whitelist.
    /// </summary>
    [XmlElement]
    public int AppFilteringMode { get; set; }

    [XmlArray("AllowedApps")]
    [XmlArrayItem("App")]
    public List<string> AllowedApps { get; set; }

    [XmlArray("BlockedApps")]
    [XmlArrayItem("App")]
    public List<string> BlockedApps { get; set; }

    [XmlElement]
    public bool Startup { get; set; }

    [XmlElement]
    public bool MediaFlyoutEnabled { get; set; }

    [XmlElement]
    public bool TaskbarWidgetEnabled { get; set; }

    [XmlElement]
    public string AppLanguage { get; set; }

    [XmlElement]
    public bool CompactLayout { get; set; }

    [XmlElement]
    public int FlyoutSelectedMonitor { get; set; }

    [XmlElement]
    public int Position { get; set; }

    [XmlElement]
    public int FlyoutAnimationSpeed { get; set; } = 2;

    [XmlElement]
    public bool PlayerInfoEnabled { get; set; } = true;

    [XmlElement]
    public bool RepeatEnabled { get; set; }

    [XmlElement]
    public bool ShuffleEnabled { get; set; }

    [XmlElement]
    public bool MediaFlyoutAlwaysDisplay { get; set; }

    [XmlElement]
    public int Duration { get; set; }

    [XmlElement]
    public bool NextUpEnabled { get; set; }

    [XmlElement]
    public int NextUpDuration { get; set; }

    [XmlElement("nIconLeftClick")]
    public int NIconLeftClick { get; set; }

    [XmlElement]
    public bool CenterTitleArtist { get; set; }

    [XmlElement]
    public int FlyoutAnimationEasingStyle { get; set; } = 2;

    [XmlElement]
    public bool LockKeysEnabled { get; set; } = true;

    [XmlElement]
    public bool LockKeysCapsEnabled { get; set; } = true;

    [XmlElement]
    public bool LockKeysNumEnabled { get; set; } = true;

    [XmlElement]
    public bool LockKeysScrollEnabled { get; set; } = true;

    [XmlElement]
    public int LockKeysDuration { get; set; }

    [XmlElement]
    public int AppTheme { get; set; }

    [XmlElement]
    public bool MediaFlyoutVolumeKeysExcluded { get; set; }

    [XmlElement("nIconSymbol")]
    public bool NIconSymbol { get; set; }

    [XmlElement]
    public bool NIconHide { get; set; }

    [XmlElement]
    public bool DisableIfFullscreen { get; set; } = true;

    [XmlElement("LockKeysBoldUI")]
    public bool LockKeysBoldUi { get; set; }

    [XmlElement]
    public int LockKeysMonitorPreference { get; set; }

    [XmlElement]
    public string LastKnownVersion { get; set; }

    [XmlElement]
    public bool SeekbarEnabled { get; set; }

    [XmlElement]
    public bool PauseOtherSessionsEnabled { get; set; }

    [XmlElement]
    public bool LockKeysAnimated { get; set; } = true;

    [XmlElement]
    public bool LockKeysInsertEnabled { get; set; } = true;

    [XmlElement]
    public int MediaFlyoutBackgroundBlur { get; set; }

    [XmlElement]
    public bool MediaFlyoutAcrylicWindowEnabled { get; set; } = true;

    [XmlElement]
    public bool NextUpAcrylicWindowEnabled { get; set; } = true;

    [XmlElement]
    public bool LockKeysAcrylicWindowEnabled { get; set; } = true;

    [XmlElement]
    public bool VolumeMixerAcrylicWindowEnabled { get; set; } = true;

    [XmlElement]
    public string FontFamily { get; set; }

    [XmlElement]
    public int TaskbarWidgetSelectedMonitor { get; set; }

    [XmlElement]
    public bool TaskbarWidgetAutoHide { get; set; }

    [XmlElement]
    public int TaskbarWidgetPosition { get; set; }

    [XmlElement]
    public bool TaskbarWidgetPadding { get; set; } = true;

    [XmlElement]
    public int TaskbarWidgetManualPadding { get; set; }

    [XmlElement]
    public int TaskbarWidgetBorderRadius { get; set; }

    [XmlElement]
    public int TaskbarWidgetAlbumArtRadius { get; set; }

    [XmlElement]
    public int TaskbarWidgetButtonHoverRadius { get; set; }

    [XmlElement]
    public bool TaskbarWidgetBackgroundBlur { get; set; }

    [XmlElement]
    public int TaskbarWidgetBackgroundBlurIntensity { get; set; }

    [XmlElement]
    public int TaskbarWidgetBackgroundBlurRadius { get; set; }

    [XmlElement]
    public bool TaskbarWidgetBackgroundRotate { get; set; }

    [XmlElement]
    public int TaskbarWidgetBackgroundRotateSide { get; set; }

    [XmlElement]
    public int TaskbarWidgetBackgroundRotateDirection { get; set; }

    [XmlElement]
    public bool TaskbarWidgetBackgroundRotateHighRefreshRate { get; set; }

    [XmlElement]
    public int TaskbarWidgetBackgroundRotateDuration { get; set; }

    [XmlElement]
    public int TaskbarWidgetBackgroundRotateSize { get; set; }

    [XmlElement]
    public bool TaskbarWidgetHideCompletely { get; set; }

    [XmlElement]
    public bool TaskbarWidgetFixedWidth { get; set; }

    [XmlElement]
    public int TaskbarWidgetFixedWidthPx { get; set; }

    [XmlElement]
    public bool TaskbarWidgetShowAlbumArt { get; set; } = true;

    [XmlElement]
    public bool TaskbarWidgetShowPauseOverlay { get; set; } = true;

    [XmlElement]
    public bool TaskbarWidgetControlsEnabled { get; set; }

    [XmlElement]
    public int TaskbarWidgetControlsPosition { get; set; } = 1;

    [XmlElement]
    public bool TaskbarWidgetClickOpensFlyout { get; set; } = true;

    [XmlElement]
    public bool TaskbarWidgetAnimated { get; set; } = true;

    [XmlElement]
    public int TaskbarWidgetSongChangeAnimation { get; set; }

    [XmlElement]
    public bool TaskbarWidgetResizeAnimated { get; set; } = true;

    [XmlElement]
    public string TaskbarWidgetFontFamily { get; set; }

    [XmlElement]
    public int TaskbarWidgetTextStyle { get; set; }

    [XmlElement]
    public int TaskbarWidgetTitleFontSize { get; set; }

    [XmlElement]
    public int TaskbarWidgetArtistFontSize { get; set; }

    [XmlElement]
    public bool TaskbarWidgetScrollingEnabled { get; set; }

    [XmlElement]
    public bool TaskbarWidgetScrollingTextLoopForever { get; set; }

    [XmlElement]
    public int TaskbarWidgetScrollingTextSpeed { get; set; }

    [XmlElement]
    public bool TaskbarVisualizerEnabled { get; set; }

    [XmlElement]
    public int TaskbarVisualizerPosition { get; set; } = 1;

    [XmlElement]
    public bool TaskbarVisualizerClickable { get; set; } = true;

    [XmlIgnore]
    public bool TaskbarVisualizerHasContent { get; set; }

    [XmlElement]
    public int TaskbarVisualizerBarCount { get; set; }

    [XmlElement]
    public bool TaskbarVisualizerCenteredBars { get; set; }

    [XmlElement]
    public bool TaskbarVisualizerBaseline { get; set; }

    [XmlElement]
    public int TaskbarVisualizerAudioSensitivity { get; set; }

    [XmlElement]
    public bool TaskbarVisualizerBaselineAutoHide { get; set; }

    [XmlElement]
    public bool TaskbarVisualizerHighRefreshRate { get; set; }

    [XmlElement]
    public bool VolumeControlEnabled { get; set; }

    [XmlElement]
    public bool VolumeControlAboveMediaFlyout { get; set; }

    [XmlElement]
    public int VolumeControlDuration { get; set; }

    [XmlElement]
    public bool VolumeMixerEnabled { get; set; }

    [XmlElement]
    public bool VolumeMixerHighlightActiveApps { get; set; }

    [XmlElement]
    public int TaskbarVisualizerAudioPeakLevel { get; set; }

    [XmlElement]
    public int TaskbarVisualizerSmoothing { get; set; }

    [XmlElement]
    public uint AcrylicBlurOpacity { get; set; }

    [XmlElement]
    public bool UseAlbumArtAsAccentColor { get; set; }

    [XmlElement]
    public uint AlbumAccentDesaturationThreshold { get; set; }

    [XmlElement]
    public uint AlbumAccentDesaturationAmount { get; set; }

    [XmlElement]
    public bool LegacyTaskbarWidthEnabled { get; set; }
}
