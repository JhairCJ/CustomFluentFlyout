// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Models;
using FluentFlyoutWPF.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;

namespace FluentFlyoutWPF.ViewModels;

/**
 * User Settings data model.
 */
public partial class UserSettings : ObservableObject
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // List of non-XmlIgnore property names
    private static readonly HashSet<string> PersistedPropertyNames =
    [
        .. typeof(UserSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && property.GetCustomAttribute<XmlIgnoreAttribute>() is null)
            .Select(property => property.Name)
    ];

    /// <summary>
    /// Use a compact layout
    /// </summary>
    [ObservableProperty]
    public partial bool CompactLayout { get; set; }

    /// <summary>
    /// Flyout Target Display
    /// </summary>
    [ObservableProperty]
    public partial int FlyoutSelectedMonitor { get; set; }

    /// <summary>
    /// Flyout position on screen
    /// </summary>
    [ObservableProperty]
    public partial int Position { get; set; }

    /// <summary>
    /// Scale for flyout animation speed
    /// </summary>
    [ObservableProperty]
    public partial int FlyoutAnimationSpeed { get; set; }

    /// <summary>
    /// Show player information in the flyout
    /// </summary>
    [ObservableProperty]
    public partial bool PlayerInfoEnabled { get; set; }

    /// <summary>
    /// Enable repeat button
    /// </summary>
    [ObservableProperty]
    public partial bool RepeatEnabled { get; set; }

    /// <summary>
    /// Enable shuffle button
    /// </summary>
    [ObservableProperty]
    public partial bool ShuffleEnabled { get; set; }

    /// <summary>
    /// Start minimized to tray when Windows starts
    /// </summary>
    [ObservableProperty]
    public partial bool Startup { get; set; }

    /// <summary>
    /// MediaFlyout Always Display
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDurationEditable))]
    public partial bool MediaFlyoutAlwaysDisplay { get; set; }

    [XmlIgnore] public bool IsDurationEditable => !MediaFlyoutAlwaysDisplay;

    /// <summary>
    /// Flyout display duration (milliseconds)
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial int Duration { get; set; }

    [XmlIgnore]
    public string DurationText
    {
        get => Duration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                Duration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                Duration = 3000;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Enable the 'Next Up' flyout (experimental)
    /// </summary>
    [ObservableProperty]
    public partial bool NextUpEnabled { get; set; }

    /// <summary>
    /// 'Next Up' flyout display duration (milliseconds)
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextUpDurationText))]
    public partial int NextUpDuration { get; set; }

    [XmlIgnore]
    public string NextUpDurationText
    {
        get => NextUpDuration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                NextUpDuration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                NextUpDuration = 2000;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Tray icon left-click behavior
    /// </summary>
    [ObservableProperty]
    [XmlElement(ElementName = "nIconLeftClick")]
    public partial int NIconLeftClick { get; set; }

    /// <summary>
    /// Center the title and artist text
    /// </summary>
    [ObservableProperty]
    public partial bool CenterTitleArtist { get; set; }

    /// <summary>
    /// Animation easing style index
    /// </summary>
    [ObservableProperty]
    public partial int FlyoutAnimationEasingStyle { get; set; }

    /// <summary>
    /// Enable lock keys flyout (shows Caps/Num/Scroll status)
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysEnabled { get; set; }

    [ObservableProperty]
    public partial bool LockKeysCapsEnabled { get; set; }

    [ObservableProperty]
    public partial bool LockKeysNumEnabled { get; set; }

    [ObservableProperty]
    public partial bool LockKeysScrollEnabled { get; set; }

    /// <summary>
    /// Lock keys flyout display duration (milliseconds)
    /// </summary>

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockKeysDurationText))]
    public partial int LockKeysDuration { get; set; }

    [XmlIgnore]
    public string LockKeysDurationText
    {
        get => LockKeysDuration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                LockKeysDuration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                LockKeysDuration = 2000;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// App theme. 0 for default, 1 for light, 2 for dark.
    /// </summary>
    [ObservableProperty]
    public partial int AppTheme { get; set; }

    /// <summary>
    /// Enable media flyout
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutEnabled { get; set; }

    /// <summary>
    /// Exclude volume keys from triggering media flyout
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutVolumeKeysExcluded { get; set; }

    /// <summary>
    /// Use symbol-style tray icon
    /// </summary>
    [ObservableProperty]
    [XmlElement(ElementName = "nIconSymbol")]
    public partial bool NIconSymbol { get; set; }

    /// <summary>
    /// Hide tray icon completely
    /// </summary>
    [ObservableProperty]
    public partial bool NIconHide { get; set; }

    /// <summary>
    /// Disable flyout when a DirectX exclusive fullscreen app is detected
    /// </summary>
    [ObservableProperty]
    public partial bool DisableIfFullscreen { get; set; }

    /// <summary>
    /// Use bold symbol and font in the lock keys flyout
    /// </summary>
    [ObservableProperty]
    [XmlElement(ElementName = "LockKeysBoldUI")]
    public partial bool LockKeysBoldUi { get; set; }

    /// Selects which monitor to use for the lock keys flyout when multiple monitors are in use.
    /// 0 = Default behavior, 1 = Monitor containing the focused window, 2 = Monitor containing the cursor.
    [ObservableProperty]
    public partial int LockKeysMonitorPreference { get; set; }

    /// <summary>
    /// Determines if the user has updated to a new version
    /// </summary>
    [ObservableProperty]
    public partial string LastKnownVersion { get; set; }

    /// <summary>
    /// Show seekbar if the player supports it
    /// </summary>
    [ObservableProperty]
    public partial bool SeekbarEnabled { get; set; }

    /// <summary>
    /// Pause other media sessions when focusing a new one
    /// </summary>
    [ObservableProperty]
    public partial bool PauseOtherSessionsEnabled { get; set; }

    /// <summary>
    /// Enable subtle animations for the lock keys flyout indicator
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysAnimated { get; set; }

    /// <summary>
    /// Show LockKeys flyout when the Insert key is pressed
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysInsertEnabled { get; set; }

    /// <summary>
    /// Preset for media flyout background blur styles
    /// </summary>
    [ObservableProperty]
    public partial int MediaFlyoutBackgroundBlur { get; set; }

    /// <summary>
    /// Enable acrylic blur effect on the flyout window
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutAcrylicWindowEnabled { get; set; }

    /// <summary>
    /// Enable acrylic blur effect on the Next Up window
    /// </summary>
    [ObservableProperty]
    public partial bool NextUpAcrylicWindowEnabled { get; set; }

    /// <summary>
    /// Enable acrylic blur effect on the Lock Keys window
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysAcrylicWindowEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeMixerAcrylicWindowEnabled { get; set; }

    /// <summary>
    /// User's preferred app language (e.g., "system" for system default)
    /// </summary>
    [ObservableProperty]
    public partial string AppLanguage { get; set; }

    /// <summary>
    /// Language Options
    /// </summary>
    [XmlIgnore]
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = [];

    [XmlIgnore]
    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial FlowDirection FlowDirection { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial string FontFamily { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget is enabled
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetEnabled { get; set; }

    /// <summary>Whether clicking the song block can expand the taskbar widget.</summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetExpandOnClick { get; set; }

    /// <summary>Additional shading of the expanded widget, from 0 to 100 percent.</summary>
    [ObservableProperty]
    public partial int TaskbarWidgetExpandedBackgroundDarkness { get; set; }


    /// <summary>
    /// Widget Target Display
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetSelectedMonitor { get; set; }

    /// <summary>
    /// Autohide Widget after a few milliseconds after pause 
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetAutoHide { get; set; }

    /// <summary>
    /// Gets or sets the position of the taskbar widget, represented as an integer value.
    /// 0: Left, 1: Center, 2: Right
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetPosition { get; set; }

    /// <summary>
    /// Determines whether padding should be applied to the taskbar widget for the native Windows Widgets button
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetPadding { get; set; }

    /// <summary>
    /// Manual padding value in pixels applied to the taskbar widget
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetManualPaddingText))]
    public partial int TaskbarWidgetManualPadding { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetManualPaddingText
    {
        get => TaskbarWidgetManualPadding.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetManualPadding = result switch
                {
                    > 9999 => 9999,
                    < -9999 => -9999,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetManualPadding = 0;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Corner radius in pixels applied to the taskbar widget, controlling how rounded its corners are.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBorderRadiusText))]
    public partial int TaskbarWidgetBorderRadius { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetBorderRadiusText
    {
        get => TaskbarWidgetBorderRadius.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetBorderRadius = result switch
                {
                    > 40 => 40,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetBorderRadius = 6;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Corner radius in pixels applied to the album art inside the taskbar widget.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetAlbumArtRadiusText))]
    public partial int TaskbarWidgetAlbumArtRadius { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetAlbumArtRadiusText
    {
        get => TaskbarWidgetAlbumArtRadius.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetAlbumArtRadius = result switch
                {
                    > 20 => 20,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetAlbumArtRadius = 5;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Corner radius in pixels applied to the hover background of the media buttons in the taskbar widget.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetButtonHoverRadiusText))]
    public partial int TaskbarWidgetButtonHoverRadius { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetButtonHoverRadiusText
    {
        get => TaskbarWidgetButtonHoverRadius.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetButtonHoverRadius = result switch
                {
                    > 20 => 20,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetButtonHoverRadius = 6;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets a value indication whether the taskbar widget background should have a blur effect
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBackgroundBlurIntensityEnabled))]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBackgroundBlurRadiusEnabled))]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBackgroundRotateEnabled))]
    public partial bool TaskbarWidgetBackgroundBlur { get; set; }

    /// <summary>
    /// Gets or sets the intensity (0-100) of the taskbar widget background blur
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetBackgroundBlurIntensity { get; set; }

    /// <summary>
    /// Gets or sets the blur radius (0-150) of the taskbar widget background blur
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetBackgroundBlurRadius { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget background should rotate continuously
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBackgroundRotateEnabled))]
    public partial bool TaskbarWidgetBackgroundRotate { get; set; }

    /// <summary>
    /// Gets or sets which side of the rotating album is visible in the widget (0 = left, 1 = right)
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetBackgroundRotateSide { get; set; }

    /// <summary>
    /// Gets or sets the rotation direction of the background disc (0 = down, 1 = up)
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetBackgroundRotateDirection { get; set; }

    /// <summary>
    /// Whether the rotating background runs at the monitor's refresh rate.
    /// When false, the rotation animation is capped at 30 FPS to reduce GPU/CPU cost.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetBackgroundRotateHighRefreshRate { get; set; }

    /// <summary>
    /// Gets or sets the number of seconds one full background rotation takes
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBackgroundRotateDurationText))]
    public partial int TaskbarWidgetBackgroundRotateDuration { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetBackgroundRotateDurationText => TaskbarWidgetBackgroundRotateDuration > 1 ? $"{TaskbarWidgetBackgroundRotateDuration} seconds" : $"{TaskbarWidgetBackgroundRotateDuration} second";

    /// <summary>
    /// Gets or sets the size multiplier (in percent) of the rotating album disc relative to
    /// the widget's dimensions. 300 = the disc is three times the widget's width/height.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetBackgroundRotateSizeText))]
    public partial int TaskbarWidgetBackgroundRotateSize { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetBackgroundRotateSizeText => $"{TaskbarWidgetBackgroundRotateSize}%";

    [XmlIgnore]
    public bool TaskbarWidgetBackgroundBlurIntensityEnabled => TaskbarWidgetBackgroundBlur;

    [XmlIgnore]
    public bool TaskbarWidgetBackgroundBlurRadiusEnabled => TaskbarWidgetBackgroundBlur;

    [XmlIgnore]
    public bool TaskbarWidgetBackgroundRotateEnabled => TaskbarWidgetBackgroundBlur;

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget should be completely hidden from view when no media is playing.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetHideCompletely { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget should always be sized at its
    /// maximum width, so right-aligned controls don't shift when the song changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetFixedWidthPxEnabled))]
    public partial bool TaskbarWidgetFixedWidth { get; set; }

    /// <summary>
    /// Gets or sets the fixed width in pixels (at 100% DPI) applied to the taskbar widget when
    /// <see cref="TaskbarWidgetFixedWidth"/> is enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetFixedWidthPxText))]
    public partial int TaskbarWidgetFixedWidthPx { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetFixedWidthPxText
    {
        get => TaskbarWidgetFixedWidthPx.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetFixedWidthPx = result switch
                {
                    > 216 => 216,
                    < 80 => 80,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetFixedWidthPx = 216;
            }

            OnPropertyChanged();
        }
    }

    [XmlIgnore]
    public bool TaskbarWidgetFixedWidthPxEnabled => TaskbarWidgetFixedWidth;

    /// <summary>
    /// Gets or sets a value indicating whether the album art thumbnail next to the song
    /// title and artist should be shown on the taskbar widget.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetShowAlbumArt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pause icon overlay should be completely hidden from view.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetShowPauseOverlay { get; set; }

    /// <summary>
    /// Whether taskbar widget controls (pause, previous, next) are enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetControlsEnabled { get; set; }

    /// <summary>
    /// Position of the taskbar widget controls. 0: Left, 1: Right
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetControlsPosition { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether clicking the taskbar widget opens the media flyout.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetClickOpensFlyout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget should play animations.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetAnimated { get; set; }

    /// <summary>
    /// Song-change animation style for the taskbar widget. 0: Crossfade (snapshot fade),
    /// 1: Slide (old text slides out left, new text slides in from the right).
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetSongChangeAnimation { get; set; }

    /// <summary>Shared album transition: 0 crossfade, 1 flip.</summary>
    [ObservableProperty]
    public partial int AlbumArtChangeAnimation { get; set; }


    /// <summary>
    /// Whether the taskbar widget smoothly morphs its width when the song changes.
    /// When false, width changes snap instantly (interior and exterior together).
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetResizeAnimated { get; set; }

    /// <summary>
    /// Font family used only by the taskbar widget (song title and artist).
    /// Bundled display names (Inter, Manrope, …) work on any PC; anything else
    /// is treated as a system font name and can be typed freely.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetFontSource))]
    public partial string TaskbarWidgetFontFamily { get; set; }

    /// <summary>
    /// Resolved widget typeface for XAML bindings (pack URI for bundled fonts).
    /// </summary>
    [XmlIgnore]
    public FontFamily TaskbarWidgetFontSource => WidgetFonts.Resolve(TaskbarWidgetFontFamily);

    /// <summary>
    /// Text style preset for the widget song/artist rows.
    /// 0: Modern (semibold title, soft artist), 1: Classic, 2: Bold, 3: Soft italic artist.
    /// Sizes stay independent (<see cref="TaskbarWidgetTitleFontSize"/> /
    /// <see cref="TaskbarWidgetArtistFontSize"/>); the preset only sets weights,
    /// artist opacity and artist italic.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetTextStyle { get; set; }

    /// <summary>
    /// Song title font size (DIPs) in the taskbar widget.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetTitleFontSize { get; set; }

    /// <summary>
    /// Artist font size (DIPs) in the taskbar widget.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetArtistFontSize { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget scrolling text (marquee) is enabled for long titles.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget scrolling text should loop forever.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingTextLoopForever { get; set; }

    /// <summary>
    /// Gets or sets the speed of the taskbar widget scrolling text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetScrollingTextSpeedText))]
    public partial int TaskbarWidgetScrollingTextSpeed { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetScrollingTextSpeedText
    {
        get => TaskbarWidgetScrollingTextSpeed.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetScrollingTextSpeed = result switch
                {
                    > 100 => 100,
                    < 1 => 1,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetScrollingTextSpeed = 20;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar visualizer is enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerEnabled { get; set; }

    /// <summary>
    /// Fluent Island: muestra la isla superior central.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandFloatingStyleEnabled))]
    public partial bool IslandEnabled { get; set; }

    /// <summary>
    /// Fluent Island: shows the island's translucent white border.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandBorderEnabled { get; set; }

    /// <summary>
    /// Fluent Island corner radius in pixels.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandBorderRadiusText))]
    public partial int IslandBorderRadius { get; set; }

    [XmlIgnore]
    public string IslandBorderRadiusText
    {
        get => IslandBorderRadius.ToString();
        set
        {
            if (int.TryParse(value, out var result))
                IslandBorderRadius = Math.Clamp(result, 0, 40);
            else IslandBorderRadius = 10;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Island corner radius in the compact state (pixels, 0-40).
    /// Value -1 = not migrated: it inherits <see cref="IslandBorderRadius"/> on load.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandCompactBorderRadiusText))]
    public partial int IslandCompactBorderRadius { get; set; }

    [XmlIgnore]
    public string IslandCompactBorderRadiusText
    {
        get => Math.Clamp(IslandCompactBorderRadius, 0, 40).ToString();
        set
        {
            if (int.TryParse(value, out var result))
                IslandCompactBorderRadius = Math.Clamp(result, 0, 40);
            else IslandCompactBorderRadius = 10;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Island corner radius in the expanded state (pixels, 0-40).
    /// Value -1 = not migrated: it inherits <see cref="IslandBorderRadius"/> on load.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandExpandedBorderRadiusText))]
    public partial int IslandExpandedBorderRadius { get; set; }

    [XmlIgnore]
    public string IslandExpandedBorderRadiusText
    {
        get => Math.Clamp(IslandExpandedBorderRadius, 0, 40).ToString();
        set
        {
            if (int.TryParse(value, out var result))
                IslandExpandedBorderRadius = Math.Clamp(result, 0, 40);
            else IslandExpandedBorderRadius = 10;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Edge blend curve in notch mode, compact state (pixels, 0-20).
    /// Value -1 = not migrated: 6 is used on load.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandNotchFilletCompactText))]
    public partial int IslandNotchFilletCompact { get; set; }

    [XmlIgnore]
    public string IslandNotchFilletCompactText
    {
        get => Math.Clamp(IslandNotchFilletCompact, 0, 20).ToString();
        set
        {
            if (int.TryParse(value, out var result))
                IslandNotchFilletCompact = Math.Clamp(result, 0, 20);
            else IslandNotchFilletCompact = 6;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Edge blend curve in notch mode, expanded state (pixels, 0-20).
    /// Value -1 = not migrated: 10 is used on load.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandNotchFilletExpandedText))]
    public partial int IslandNotchFilletExpanded { get; set; }

    [XmlIgnore]
    public string IslandNotchFilletExpandedText
    {
        get => Math.Clamp(IslandNotchFilletExpanded, 0, 20).ToString();
        set
        {
            if (int.TryParse(value, out var result))
                IslandNotchFilletExpanded = Math.Clamp(result, 0, 20);
            else IslandNotchFilletExpanded = 10;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Corner radius of the artwork and of its media change overlay.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandAlbumArtRadiusText))]
    public partial int IslandAlbumArtRadius { get; set; }

    [XmlIgnore]
    public string IslandAlbumArtRadiusText
    {
        get => IslandAlbumArtRadius.ToString();
        set
        {
            if (int.TryParse(value, out var result))
                IslandAlbumArtRadius = Math.Clamp(result, 0, 32);
            else IslandAlbumArtRadius = 8;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Expanded Island width in pixels.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandExpandedWidthText))]
    public partial int IslandExpandedWidth { get; set; }

    [XmlIgnore]
    public string IslandExpandedWidthText
    {
        get => (IslandExpandedWidth > 0 ? Math.Clamp(IslandExpandedWidth, 280, 600) : 320).ToString();
        set
        {
            IslandExpandedWidth = int.TryParse(value, out var result)
                ? Math.Clamp(result, 280, 600)
                : 320;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Expanded Island height in pixels when it uses the floating island style.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandExpandedHeightText))]
    public partial int IslandExpandedHeight { get; set; }

    [XmlIgnore]
    public string IslandExpandedHeightText
    {
        get => (IslandExpandedHeight > 0 ? Math.Clamp(IslandExpandedHeight, 100, 220) : 120).ToString();
        set
        {
            IslandExpandedHeight = int.TryParse(value, out var result)
                ? Math.Clamp(result, 100, 220)
                : 120;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Fluent Island: enables the artwork-based blurred background.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandBackgroundBlurIntensityEnabled))]
    [NotifyPropertyChangedFor(nameof(IslandBackgroundBlurRadiusEnabled))]
    [NotifyPropertyChangedFor(nameof(IslandBackgroundRotateEnabled))]
    public partial bool IslandBackgroundBlur { get; set; }

    [ObservableProperty]
    public partial int IslandBackgroundBlurIntensity { get; set; }

    [ObservableProperty]
    public partial int IslandBackgroundBlurRadius { get; set; }

    /// <summary>
    /// Fluent Island: rotates the blurred background continuously.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandBackgroundRotateEnabled))]
    public partial bool IslandBackgroundRotate { get; set; }

    [ObservableProperty]
    public partial int IslandBackgroundRotateSide { get; set; }

    [ObservableProperty]
    public partial int IslandBackgroundRotateDirection { get; set; }

    [ObservableProperty]
    public partial bool IslandBackgroundRotateHighRefreshRate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandBackgroundRotateDurationText))]
    public partial int IslandBackgroundRotateDuration { get; set; }

    [XmlIgnore]
    public string IslandBackgroundRotateDurationText => IslandBackgroundRotateDuration > 1 ? $"{IslandBackgroundRotateDuration} segundos" : $"{IslandBackgroundRotateDuration} segundo";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandBackgroundRotateSizeText))]
    public partial int IslandBackgroundRotateSize { get; set; }

    [XmlIgnore]
    public string IslandBackgroundRotateSizeText => $"{IslandBackgroundRotateSize}%";

    [XmlIgnore]
    public bool IslandBackgroundBlurIntensityEnabled => IslandBackgroundBlur;

    [XmlIgnore]
    public bool IslandBackgroundBlurRadiusEnabled => IslandBackgroundBlur;

    [XmlIgnore]
    public bool IslandBackgroundRotateEnabled => IslandBackgroundBlur;

    /// <summary>
    /// Fluent Island (001 MOD RF-2): 0 = "Visible while active" (shown while any
    /// enabled feature is active),
    /// 1 = "Temporary notice" (shows the event's content for 1-10 s and goes back to
    /// rest). The "Always in place" mode was removed (001 REMOVED).
    /// </summary>
    [ObservableProperty]
    public partial int IslandVisibilityMode { get; set; }

    /// <summary>
    /// Fluent Island: how it expands in "always in place".
    /// 0 = click in the middle, 1 = wheel down, 2 = both.
    /// </summary>
    [ObservableProperty]
    public partial int IslandExpandTrigger { get; set; }

    /// <summary>
    /// Fluent Island: temporary notice duration in ms (1000..10000).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandVisibilityDurationText))]
    public partial int IslandVisibilityDuration { get; set; }

    [XmlIgnore]
    public string IslandVisibilityDurationText
    {
        get => (Math.Clamp(IslandVisibilityDuration, 1000, 10000) / 1000).ToString();
        set
        {
            if (int.TryParse(value, out var sec))
                IslandVisibilityDuration = Math.Clamp(sec * 1000, 1000, 10000);
            else IslandVisibilityDuration = 4000;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IslandVisibilityDurationSeconds));
        }
    }

    [XmlIgnore]
    public double IslandVisibilityDurationSeconds
    {
        get => Math.Clamp(IslandVisibilityDuration, 1000, 10000) / 1000.0;
        set
        {
            IslandVisibilityDuration = Math.Clamp((int)Math.Round(value * 1000), 1000, 10000);
            OnPropertyChanged(nameof(IslandVisibilityDurationText));
        }
    }

    partial void OnIslandVisibilityDurationChanged(int oldValue, int newValue)
    {
        int fixedVal = newValue == 0 ? 4000 : Math.Clamp(newValue, 1000, 10000);
        if (fixedVal != newValue) IslandVisibilityDuration = fixedVal;
        OnPropertyChanged(nameof(IslandVisibilityDurationText));
        OnPropertyChanged(nameof(IslandVisibilityDurationSeconds));
    }

    /// <summary>
    /// Fluent Island: shows the island when media starts, resumes, or pauses.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandShowOnPlayPause { get; set; }

    /// <summary>
    /// Fluent Island: shows the island when media is paused.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandShowOnPause { get; set; }

    /// <summary>
    /// Fluent Island: shows the island when the media track changes.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandShowOnTrackChange { get; set; }

    /// <summary>
    /// Fluent Island (001 MOD RF-2): "return to inactive" toggle for rest.
    /// Enabled (default): when the notice expires or activity drops, the inactive
    /// piece (narrow black one) stays visible. Disabled: the island hides
    /// completely (nothing). Persists across restarts.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandReturnToInactive { get; set; }

    /// <summary>
    /// Fluent Island (change island-ultra-compacto): ULTRA COMPACT mode. With the
    /// capsule folded back only its two EXTREMES are seen - the left one and the right
    /// one -: in the media controls that leaves the artwork and the equalizer, with no
    /// title or artist. The resting presence is minimal on purpose, meant to be used
    /// with "return to inactive" OFF: the Island only appears when there is something
    /// to show and, since there is no piece to point at, the top strip (from the
    /// activity line to the edge) is what brings it back expanded.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandUltraCompact { get; set; }

    /// <summary>
    /// Fluent Island (001 MOD RF-6): "media pause counts as active" setting.
    /// Enabled (default): a paused session keeps visibility in "Visible while
    /// active" and shows its controls. Disabled: the pause does not count as
    /// activity, although click access is kept.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandPauseCountsActive { get; set; }

    /// <summary>
    /// Fluent Island: grey line indicating that the Island is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandActivityLine { get; set; }

    /// <summary>
    /// Fluent Island: keep the pointer door with the Island HIDDEN. Off (default) the
    /// edge strip only exists where something is visible - the box or its grey line -
    ///: with neither, the pointer does nothing there, which was exactly the annoying
    /// invisible trigger. On, the edge strip keeps opening the Island even when
    /// nothing is drawn (a deliberately invisible door).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandHiddenAccess { get; set; }

    /// <summary>
    /// Fluent Island: 0 = floating island, 1 = top notch (it hangs from the top edge).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandFloatingStyleEnabled))]
    public partial int IslandStyle { get; set; }

    /// <summary>
    /// Fluent Island: height in px of the invisible strip that detects the mouse (4-30).
    /// </summary>
    [ObservableProperty]
    public partial int IslandHoverTolerance { get; set; }

    /// <summary>
    /// Floating Fluent Island: px from the top edge down to the grey line (0-60).
    /// </summary>
    [ObservableProperty]
    public partial int IslandLineTopOffset { get; set; }

    /// <summary>
    /// Floating Fluent Island: px from the top edge down to the island (0-80), iPhone style.
    /// </summary>
    [ObservableProperty]
    public partial int IslandTopOffset { get; set; }

    /// <summary>
    /// Fluent Island: invisible px on each side of the line that keep detecting (0-80). 0 = line only (120px).
    /// </summary>
    [ObservableProperty]
    public partial int IslandHoverToleranceHorizontal { get; set; }

    /// <summary>
    /// Fluent Island: invisible px downwards from the line that keep detecting (0-40). 0 = above the line only.
    /// </summary>
    [ObservableProperty]
    public partial int IslandHoverToleranceVertical { get; set; }

    [XmlIgnore]
    public bool IslandFloatingStyleEnabled => IslandEnabled && IslandStyle == 0;

    /// <summary>
    /// Fluent Island: functional equalizer (real audio).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandEqEnabled { get; set; }

    /// <summary>
    /// Fluent Island: mirrors the equalizer bars around the center line.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandEqCenteredBars { get; set; }

    /// <summary>
    /// Fluent Island: equalizer bar count (1-10).
    /// </summary>
    [ObservableProperty]
    public partial int IslandEqBarCount { get; set; }

    [ObservableProperty]
    public partial double IslandEqBarCornerRadius { get; set; } = 6;

    /// <summary>
    /// Fluent Island: equalizer sensitivity (1-3).
    /// </summary>
    [ObservableProperty]
    public partial int IslandEqSensitivity { get; set; }

    /// <summary>
    /// Fluent Island: equalizer smoothing (0-100).
    /// </summary>
    [ObservableProperty]
    public partial int IslandEqSmoothing { get; set; }

    /// <summary>
    /// Fluent Island: animations (appearance and morph).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandAnimated { get; set; }

    /// <summary>
    /// Fluent Island: typeface family shared by the 3 texts
    /// (compact song, expanded song, expanded artist).
    /// The bundled names (Inter, Manrope, ...) work on any PC;
    /// any other value is treated as a system font.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandFontSource))]
    public partial string IslandFontFamily { get; set; }

    /// <summary>
    /// Resolved typeface for XAML bindings (pack URI for the bundled ones).
    /// </summary>
    [XmlIgnore]
    public FontFamily IslandFontSource => WidgetFonts.Resolve(IslandFontFamily);

    /// <summary>
    /// Fluent Island: text style preset (0 Modern, 1 Classic, 2 Bold, 3 Soft).
    /// It controls weight, artist opacity and italics; sizes are separate.
    /// </summary>
    [ObservableProperty]
    public partial int IslandTextStyle { get; set; }

    /// <summary>
    /// Fluent Island: song text size in the compact island (DIPs, 10-24).
    /// </summary>
    [ObservableProperty]
    public partial int IslandCompactTitleFontSize { get; set; }

    /// <summary>
    /// Fluent Island: song text size in the expanded island (DIPs, 10-24).
    /// </summary>
    [ObservableProperty]
    public partial int IslandExpandedTitleFontSize { get; set; }

    /// <summary>
    /// Fluent Island: artist text size in the expanded island (DIPs, 10-24).
    /// </summary>
    [ObservableProperty]
    public partial int IslandExpandedArtistFontSize { get; set; }

    /// <summary>
    /// Island media content: feature enabled (independent of the Media Flyout
    /// window's toggle).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandMediaEnabled { get; set; }

    /// <summary>
    /// Island timer: feature enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandTimerEnabled { get; set; }

    /// <summary>
    /// Island timer: shows the progress at the center of the compact view.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandTimerShowProgress { get; set; }

    /// <summary>
    /// Island timer: side arrows to switch feature.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandTimerShowArrows { get; set; }

    /// <summary>
    /// Island timer: editable presets (max. 10). They persist across restarts.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<TimerPreset> IslandTimerPresets { get; set; }

    /// <summary>
    /// Last preset validation message, in Spanish. Empty = no error.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string TimerPresetsError { get; set; }

    /// <summary>
    /// Island app tray: feature enabled (independent
    /// of the music and timer windows).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandAppsEnabled { get; set; }

    /// <summary>
    /// Island app tray: the tray's apps (max. 12) with name and path. They persist
    /// across restarts; the icon is read from the path.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<IslandApp> IslandApps { get; set; }

    /// <summary>
    /// Last tray validation message, in Spanish. Empty = no error.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string IslandAppsError { get; set; }

    /// <summary>
    /// Island screens (change island-pantallas): each entry is a screen with the
    /// features shown TOGETHER, by their id
    /// (<see cref="IslandFeatureIds"/>) joined with '+'. They are the ONLY source of
    /// order and composition for the Island: the list order is the navigation order
    /// (wheel and arrows), the order inside each screen is the presentation order
    /// (left to right), and a feature that is in no screen is not shown. Each
    /// screen carries at most <see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>
    /// features. With nothing configured one screen per feature applies, which is
    /// the historical behaviour; it self-repairs on load: unknown or repeated ids
    /// are dropped, empty screens are discarded and anything left over is
    /// reparte en pantallas nuevas.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<string> IslandScreens { get; set; }

    /// <summary>
    /// Island file shelf: feature enabled. On by default: the empty shelf is its
    /// natural state (it invites you to drop something on it).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandShelfEnabled { get; set; }

    /// <summary>
    /// Island file shelf: the parked files and folders. They persist across restarts
    /// and do NOT expire: they only leave when the user drags them out or removes
    /// them (and removing them sends them back to their original folder).
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<IslandShelfItem> IslandShelfItems { get; set; }

    /// <summary>
    /// Last shelf message, in Spanish. Empty = no error.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string IslandShelfError { get; set; }

    /// <summary>
    /// Should the Island step aside when something is fullscreen (game, borderless
    /// video, presentation or locked machine)? Yes by default: staying on top of a
    /// game is exactly what must not happen. The setting belongs to the Island, so
    /// turning off the system's "hide if fullscreen" does not override it (that one
    /// still wins as its companion).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandHideOnFullscreen { get; set; }

    /// <summary>
    /// Recordatorios de Google Calendar: funcionalidad habilitada. Con ella apagada no
    /// the calendar is read (no network, no token) and the Island does not offer it.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandCalendarEnabled { get; set; }

    /// <summary>
    /// Connected Bluetooth devices: feature enabled. With it off the watchdog stops
    /// and the Island does not offer it (no notices, no consumption). The notice for
    /// a connection is ALWAYS temporary, using the configured notice duration.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandBluetoothEnabled { get; set; }

    /// <summary>
    /// Clipboard (text and images): feature enabled. With it off the Island does not
    /// listen to the clipboard (it copies nothing into its list) and does not offer
    /// it. Copying again from the Island's list IS the user's business and keeps
    /// working even with listening off.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandClipboardEnabled { get; set; }

    /// <summary>
    /// Clipboard: how many items the list keeps (1-100). Lowering it drops the
    /// oldest ones; nothing stored touches the user until
    /// pulsa un elemento.
    /// </summary>
    [ObservableProperty]
    public partial int IslandClipboardMaxItems { get; set; }

    /// <summary>
    /// Weather: feature enabled. With it off nothing is queried over the network
    /// (not even the place lookup) and the Island does not offer it.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandWeatherEnabled { get; set; }

    /// <summary>Weather: the chosen place, exactly as shown ("Vigo, Galicia, Spain").</summary>
    [ObservableProperty]
    public partial string IslandWeatherPlace { get; set; } = "";

    /// <summary>Weather: latitude of the chosen place (0 = no place).</summary>
    [ObservableProperty]
    public partial double IslandWeatherLatitude { get; set; }

    /// <summary>Weather: longitude of the chosen place.</summary>
    [ObservableProperty]
    public partial double IslandWeatherLongitude { get; set; }

    /// <summary>Weather: state of the lookup/refresh, for the settings page.</summary>
    [ObservableProperty]
    public partial string IslandWeatherStatus { get; set; } = "";

    /// <summary>Weather: reason for the last failure (empty if all went well).</summary>
    [ObservableProperty]
    public partial string IslandWeatherError { get; set; } = "";

    /// <summary>
    /// Charger: it notifies on the Island when the machine is plugged in and when it
    /// drops to battery. With it off the power state is not observed.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandPowerEnabled { get; set; }

    /// <summary>
    /// Local voice dictation (spec 006): hold the configured key or combination, speak
    /// and release; the text is written wherever the cursor is. The model runs on
    /// the machine: there is no cloud service here.
    /// </summary>
    [ObservableProperty]
    public partial bool DictationEnabled { get; set; }

    /// <summary>
    /// Dictation hotkey: a single key ("Ctrl") or a combination ("Ctrl+Shift+M").
    /// It is held down while you speak.
    /// </summary>
    [ObservableProperty]
    public partial string DictationHotkey { get; set; } = Models.DictationHotkey.Default;

    /// <summary>Short taps latch hands-free dictation; holding still ends on release. Enabled by default.</summary>
    [ObservableProperty]
    public partial bool DictationToggleMode { get; set; } = true;

    /// <summary>
    /// Active dictation model: the file name of a model in the models folder, or the
    /// full path of a hand-added one. Empty = none downloaded yet.
    /// </summary>
    [ObservableProperty]
    public partial string DictationModel { get; set; } = "";

    /// <summary>Dictation language: "auto" (the model detects it), "es" or "en".</summary>
    [ObservableProperty]
    public partial string DictationLanguage { get; set; } = "auto";

    /// <summary>
    /// Legacy default for models that do not yet have a per-model device preference.
    /// </summary>
    [ObservableProperty]
    public partial bool DictationUseGpu { get; set; }

    /// <summary>Per-model devices. The legacy GPU flag is used only until a model is configured.</summary>
    [ObservableProperty]
    public partial List<DictationModelDevicePreference> DictationModelDevices { get; set; } = [];

    public DictationDevice GetDictationDevice(string model)
    {
        string key = DictationDevices.ModelKey(model);
        var preference = DictationModelDevices.FirstOrDefault(item =>
            string.Equals(item.Model, key, StringComparison.OrdinalIgnoreCase));
        if (preference != null && DictationDevices.Supports(model, preference.Device))
            return preference.Device;
        return DictationUseGpu ? DictationDevices.GpuForModel(model) : DictationDevice.Cpu;
    }

    public DictationCudaVersion GetDictationCudaVersion(string model)
    {
        string key = DictationDevices.ModelKey(model);
        return DictationModelDevices.FirstOrDefault(item =>
            string.Equals(item.Model, key, StringComparison.OrdinalIgnoreCase))?.CudaVersion
            ?? DictationCudaVersion.Cuda12;
    }

    public void SetDictationDevice(string model, DictationDevice device, DictationCudaVersion? cudaVersion = null)
    {
        if (!DictationDevices.Supports(model, device))
            throw new ArgumentException("The model does not support this device", nameof(device));
        string key = DictationDevices.ModelKey(model);
        DictationModelDevices = [.. DictationModelDevices.Where(item =>
            !string.Equals(item.Model, key, StringComparison.OrdinalIgnoreCase)),
            new DictationModelDevicePreference
            {
                Model = key, Device = device,
                CudaVersion = cudaVersion ?? GetDictationCudaVersion(model),
            }];
    }

    /// <summary>
    /// Keeps the model weights loaded between dictations to avoid paying the initial
    /// load when several phrases are dictated in a row.
    /// </summary>
    [NotifyPropertyChangedFor(nameof(DictationAutoReleaseEnabled))]
    [ObservableProperty]
    public partial bool DictationKeepModelLoaded { get; set; }

    /// <summary>Idle seconds before releasing the dictation resources (15-600).</summary>
    [NotifyPropertyChangedFor(nameof(DictationUnloadDelayText))]
    [ObservableProperty]
    public partial int DictationUnloadDelaySeconds { get; set; }

    /// <summary>Whether automatic release timing is active.</summary>
    [XmlIgnore]
    public bool DictationAutoReleaseEnabled => !DictationKeepModelLoaded;

    /// <summary>Short text showing the chosen time next to the slider.</summary>
    [XmlIgnore]
    public string DictationUnloadDelayText => $"{DictationUnloadDelaySeconds} s";

    /// <summary>
    /// Google Calendar: reminder lead time in minutes (1-60). The notice stays alive
    /// until two minutes after the start.
    /// </summary>
    [ObservableProperty]
    public partial int GoogleCalendarReminderMinutes { get; set; }

    /// <summary>Google Calendar: how often the calendar is re-read, in minutes (1-60).</summary>
    [ObservableProperty]
    public partial int GoogleCalendarRefreshMinutes { get; set; }

    /// <summary>Google Calendar: how many days ahead are read (1-14).</summary>
    [ObservableProperty]
    public partial int GoogleCalendarDaysAhead { get; set; }

    /// <summary>
    /// The user's OAuth client id ("Desktop app" type): it is the Google project
    /// that authorizes the app, not an app credential.
    /// </summary>
    [ObservableProperty]
    public partial string GoogleCalendarClientId { get; set; }

    /// <summary>
    /// OAuth client secret. Google requires it for desktop apps too and it is not
    /// confidential (it travels in the binary of any installed app), but it is
    /// stored with the settings like everything else.
    /// </summary>
    [ObservableProperty]
    public partial string GoogleCalendarClientSecret { get; set; }

    /// <summary>Current access token (renewed only with the refresh token).</summary>
    [ObservableProperty]
    public partial string GoogleCalendarAccessToken { get; set; }

    /// <summary>
    /// Refresh token: it is the session. Its presence is what defines "signed in";
    /// it is cleared on sign-out, which is what revokes the local access.
    /// </summary>
    [ObservableProperty]
    public partial string GoogleCalendarRefreshToken { get; set; }

    /// <summary>Access token expiry, in UTC.</summary>
    [ObservableProperty]
    public partial DateTime GoogleCalendarTokenExpiresUtc { get; set; }

    /// <summary>Signed-in account (the main calendar's email). Empty = not signed in.</summary>
    [ObservableProperty]
    public partial string GoogleCalendarAccount { get; set; }

    /// <summary>
    /// Last calendar error, in Spanish. Empty = no error. It is shown in settings:
    /// network or permission failures must not die in the log.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string GoogleCalendarError { get; set; }

    /// <summary>
    /// Calendar status message, in Spanish: "Connecting to Google...", "Signed
    /// in". It does not persist (it is of the moment) and it is shown in settings.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string GoogleCalendarStatus { get; set; }

    /// <summary>Is there a Google session signed in? (not persisted: deduced from the token)</summary>
    [XmlIgnore]
    public bool GoogleCalendarSignedIn => GoogleCalendarRefreshToken.Length > 0;

    partial void OnGoogleCalendarRefreshTokenChanged(string oldValue, string newValue)
    {
        // The rest of the app asks about GoogleCalendarSignedIn: when the session
        // changes that property has to be notified too.
        OnPropertyChanged(nameof(GoogleCalendarSignedIn));
        if (!_initializing) SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Returns whether app filtering is enabled or disabled.
    /// </summary>
    [ObservableProperty]
    public partial bool AppFilteringEnabled { get; set; }

    /// <summary>
    /// Returns the active filtering mode. 0 for Whitelist, 1 for Blacklist.
    /// </summary>
    [ObservableProperty]
    public partial int AppFilteringMode { get; set; }

    /// <summary>
    /// Returns a list of apps that are allowed to display media/update the taskbar.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<string> AllowedApps { get; set; }

    /// <summary>
    /// Returns a list of apps that are NOT allowed to display media/update the taskbar.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<string> BlockedApps { get; set; }

    /// <summary>
    /// Position of the visualizer, where 0 and 1 are to the left or right of the widget.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerPosition { get; set; }

    /// <summary>
    /// Whether the visualizer is clickable to open the visualizer settings page.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerClickable { get; set; }

    /// <summary>
    /// Indicates whether the visualizer has content to display, and is not persisted since it's only relevant at runtime.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial bool TaskbarVisualizerHasContent { get; set; }

    /// <summary>
    /// The number of visualizer bars to display.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerBarCount { get; set; }

    [ObservableProperty]
    public partial double TaskbarVisualizerBarCornerRadius { get; set; } = 6;

    /// <summary>
    /// Whether the visualizer should be symmetrical/mirrored.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerCenteredBars { get; set; }

    /// <summary>
    /// Gets or sets whether a bar baseline is shown.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerBaseline { get; set; }

    /// <summary>
    /// Gets or sets the audio sensitivity for the taskbar visualizer from 1 to 3, where 2 is the default.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerAudioSensitivity { get; set; }

    /// <summary>
    /// Autohide taskbar. Only does something if TaskbarVisualizerBaseline is enabled (doesn't autohide by default).
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerBaselineAutoHide { get; set; }

    /// <summary>
    /// Whether the visualizer renders at the monitor's refresh rate.
    /// When false, rendering is capped at 30 FPS.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerHighRefreshRate { get; set; }

    [ObservableProperty]
    public partial bool VolumeControlEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeControlAboveMediaFlyout { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeControlDurationText))]
    public partial int VolumeControlDuration { get; set; }

    [XmlIgnore]
    public string VolumeControlDurationText
    {
        get => VolumeControlDuration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                VolumeControlDuration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                VolumeControlDuration = 3000;
            }

            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial bool VolumeMixerEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeMixerHighlightActiveApps { get; set; }

    /// <summary>
    /// The audio peak level for the taskbar visualizer from 1 to 3.
    /// This is used to calibrate the visualizer bar height to the audio output.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerAudioPeakLevel { get; set; }

    /// <summary>
    /// Motion smoothing for the taskbar visualizer bars, 0 (snappy) to 100 (silky).
    /// Drives the attack/release time constants and the target-side EMA alpha.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerSmoothing { get; set; }

    /// <summary>
    /// Gets or sets the opacity level of the acrylic blur effect.
    /// </summary>
    [ObservableProperty]
    public partial uint AcrylicBlurOpacity { get; set; }

    [ObservableProperty]
    public partial bool UseAlbumArtAsAccentColor { get; set; }

    /// <summary>
    /// Saturation threshold (0-100) for album accent desaturation.
    /// Album colors less saturated than this are left untouched.
    /// </summary>
    [ObservableProperty]
    public partial uint AlbumAccentDesaturationThreshold { get; set; }

    /// <summary>
    /// How much (0-100) to tone down saturation above the threshold.
    /// 0 keeps the original color; 100 flattens everything above the
    /// threshold down to it. Desaturation reduces color intensity toward
    /// gray without changing brightness.
    /// </summary>
    [ObservableProperty]
    public partial uint AlbumAccentDesaturationAmount { get; set; }

    /// <summary>
    /// Determines whether to use the legacy method for calculating taskbar width for widget positioning for compatibility with other taskbar mods
    /// </summary>
    [ObservableProperty]
    public partial bool LegacyTaskbarWidthEnabled { get; set; }

    [XmlIgnore]
    private bool _initializing = true;

    public UserSettings()
    {
        foreach (var supportedLanguage in LocalizationManager.SupportedLanguages)
        {
            LanguageOptions.Add(new LanguageOption(supportedLanguage.Key, supportedLanguage.Value));
        }

        CompactLayout = false;
        FlyoutSelectedMonitor = 0;
        Position = 0;
        FlyoutAnimationSpeed = 2;
        PlayerInfoEnabled = true;
        RepeatEnabled = false;
        ShuffleEnabled = false;
        Startup = true;
        Duration = 3000;
        NextUpEnabled = false;
        NextUpDuration = 2000;
        NIconLeftClick = 0;
        CenterTitleArtist = false;
        FlyoutAnimationEasingStyle = 2;
        LockKeysEnabled = true;
        LockKeysCapsEnabled = true;
        LockKeysNumEnabled = true;
        LockKeysScrollEnabled = true;
        LockKeysDuration = 2000;
        AppTheme = 0;
        MediaFlyoutEnabled = true;
        MediaFlyoutAlwaysDisplay = false;
        MediaFlyoutVolumeKeysExcluded = false;
        NIconSymbol = false;
        NIconHide = false;
        DisableIfFullscreen = true;
        LockKeysBoldUi = false;
        LockKeysMonitorPreference = 0;
        LastKnownVersion = "";
        SeekbarEnabled = false;
        PauseOtherSessionsEnabled = false;
        LockKeysAnimated = true;
        LockKeysInsertEnabled = true;
        MediaFlyoutBackgroundBlur = 0;
        AppLanguage = "system";
        FlowDirection = FlowDirection.LeftToRight;
        FontFamily = "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI";
        MediaFlyoutAcrylicWindowEnabled = true;
        NextUpAcrylicWindowEnabled = true;
        LockKeysAcrylicWindowEnabled = true;
        VolumeMixerAcrylicWindowEnabled = true;
        TaskbarWidgetEnabled = true;
        TaskbarWidgetExpandOnClick = true;
        TaskbarWidgetExpandedBackgroundDarkness = 0;
        TaskbarWidgetSelectedMonitor = 0;
        TaskbarWidgetAutoHide = false;
        TaskbarWidgetPosition = 0;
        TaskbarWidgetPadding = true;
        TaskbarWidgetManualPadding = 0;
        TaskbarWidgetBorderRadius = 12;
        TaskbarWidgetAlbumArtRadius = 10;
        TaskbarWidgetButtonHoverRadius = 10;
        TaskbarWidgetBackgroundBlur = true;
        TaskbarWidgetBackgroundBlurIntensity = 65;
        TaskbarWidgetBackgroundBlurRadius = 30;
        TaskbarWidgetBackgroundRotate = true;
        TaskbarWidgetBackgroundRotateSide = 0;
        TaskbarWidgetBackgroundRotateDirection = 1;
        TaskbarWidgetBackgroundRotateHighRefreshRate = false;
        TaskbarWidgetBackgroundRotateDuration = 60;
        TaskbarWidgetBackgroundRotateSize = 400;
        TaskbarWidgetHideCompletely = true;
        TaskbarWidgetClickOpensFlyout = false;
        TaskbarWidgetFixedWidth = false;
        TaskbarWidgetFixedWidthPx = 200;
        TaskbarWidgetShowAlbumArt = true;
        TaskbarWidgetShowPauseOverlay = false;
        TaskbarWidgetControlsEnabled = true;
        TaskbarWidgetControlsPosition = 1;
        TaskbarWidgetAnimated = true;
        TaskbarWidgetSongChangeAnimation = 1;
        AlbumArtChangeAnimation = 1;
        TaskbarWidgetResizeAnimated = true;
        TaskbarWidgetFontFamily = "Quicksand";
        TaskbarWidgetTextStyle = 0;
        TaskbarWidgetTitleFontSize = 13;
        TaskbarWidgetArtistFontSize = 13;
        TaskbarWidgetScrollingEnabled = false;
        TaskbarWidgetScrollingTextSpeed = 20;
        TaskbarWidgetScrollingTextLoopForever = false;
        TaskbarVisualizerEnabled = true;
        IslandEnabled = true;
        IslandBorderEnabled = false;
        IslandBorderRadius = 40;
        IslandCompactBorderRadius = 12;
        IslandExpandedBorderRadius = 30;
        IslandNotchFilletCompact = 8;
        IslandNotchFilletExpanded = 20;
        IslandAlbumArtRadius = 8;
        IslandExpandedWidth = 320;
        IslandExpandedHeight = 126;
        IslandBackgroundBlur = true;
        IslandBackgroundBlurIntensity = 50;
        IslandBackgroundBlurRadius = 40;
        IslandBackgroundRotate = true;
        IslandBackgroundRotateSide = 0;
        IslandBackgroundRotateDirection = 0;
        IslandBackgroundRotateHighRefreshRate = false;
        IslandBackgroundRotateDuration = 60;
        IslandBackgroundRotateSize = 250;
        IslandStyle = 0;
        IslandHoverTolerance = 4;
        IslandHoverToleranceHorizontal = 32;
        IslandHoverToleranceVertical = 3;
        IslandLineTopOffset = 2;
        IslandTopOffset = 2;
        IslandVisibilityMode = 0;
        IslandReturnToInactive = false;
        IslandUltraCompact = true;
        IslandPauseCountsActive = true;
        IslandExpandTrigger = 2;
        IslandVisibilityDuration = 4000;
        IslandShowOnPlayPause = true;
        IslandShowOnPause = false;
        IslandShowOnTrackChange = true;
        IslandActivityLine = false;
        IslandHiddenAccess = false;
        IslandEqEnabled = true;
        IslandEqCenteredBars = true;
        IslandEqBarCount = 8;
        IslandEqBarCornerRadius = 6;
        IslandEqSensitivity = 3;
        IslandEqSmoothing = 50;
        IslandAnimated = true;
        IslandFontFamily = "Poppins";
        IslandTextStyle = 1;
        IslandCompactTitleFontSize = 12;
        IslandExpandedTitleFontSize = 12;
        IslandExpandedArtistFontSize = 12;
        IslandMediaEnabled = false;
        IslandTimerEnabled = true;
        IslandTimerShowProgress = true;
        IslandTimerShowArrows = false;
        // Emptied on purpose: the deserializer FILLS the existing collection with the
        // saved presets (it does not replace it), so seeding it here duplicated them
        // on every startup. CompleteInitialization applies the factory ones only
        // when the file brings none.
        IslandTimerPresets = [];
        TimerPresetsError = "";
        IslandAppsEnabled = true;
        // Emptied on purpose, like the presets: the XML deserializer FILLS the
        // existing collection, seeding it here would duplicate the apps.
        IslandApps = [];
        IslandAppsError = "";
        IslandShelfEnabled = true;
        // Emptied on purpose, like the tray: the XML deserializer FILLS the
        // existing collection.
        IslandShelfItems = [];
        IslandShelfError = "";
        IslandHideOnFullscreen = true;
        // Enabled as in the saved settings; without a session nothing is queried or shown.
        IslandCalendarEnabled = true;
        // Yes by default: a notice on connecting (which also uses the temporary
        // notice deadline) is exactly what is expected of the feature, and it touches
        // nothing in the system: it only observes connections.
        IslandBluetoothEnabled = true;
        // Yes by default: storing what you copy does not change what the user copies
        // (the original stays intact) and it is exactly what is expected of the
        // feature; turn it off from settings to stop listening on the spot.
        IslandClipboardEnabled = true;
        IslandClipboardMaxItems = 25;
        // The place belongs to each user and starts empty; until one is chosen,
        // weather queries no network even with the setting enabled.
        IslandWeatherEnabled = true;
        IslandWeatherPlace = "";
        IslandWeatherLatitude = 0;
        IslandWeatherLongitude = 0;
        IslandWeatherStatus = "";
        IslandWeatherError = "";
        // Yes by default: finding out the charger was unplugged (or that it started
        // charging) is exactly what is expected, and it only observes the state it
        // already knows
        // Windows.
        IslandPowerEnabled = true;
        DictationEnabled = false;
        DictationHotkey = Models.DictationHotkey.Default;
        DictationToggleMode = true;
        DictationModel = "";
        DictationLanguage = "auto";
        DictationUseGpu = false;
        DictationKeepModelLoaded = false;
        DictationUnloadDelaySeconds = 120;
        GoogleCalendarReminderMinutes = 5;
        GoogleCalendarRefreshMinutes = 5;
        GoogleCalendarDaysAhead = 7;
        GoogleCalendarClientId = "";
        GoogleCalendarClientSecret = "";
        GoogleCalendarAccessToken = "";
        GoogleCalendarRefreshToken = "";
        GoogleCalendarTokenExpiresUtc = DateTime.MinValue;        GoogleCalendarAccount = "";
        GoogleCalendarError = "";
        GoogleCalendarStatus = "";

        // Emptied on purpose, like the presets, the tray and the shelf: the XML
        // deserializer FILLS the existing collection instead of replacing it, so
        // seeding the default screens here added them on top of the user's on
        // EVERY startup (the user saw their screens plus the factory ones, and the
        // save wrote them all). CompleteInitialization applies the factory ones
        // only when the file brings none.
        IslandScreens = [];
        AppFilteringEnabled = false;
        AppFilteringMode = 0;
        TaskbarVisualizerPosition = 1;
        TaskbarVisualizerClickable = false;
        TaskbarVisualizerBarCount = 8;
        TaskbarVisualizerBarCornerRadius = 6;
        TaskbarVisualizerCenteredBars = false;
        TaskbarVisualizerBaseline = false;
        TaskbarVisualizerAudioSensitivity = 3;
        TaskbarVisualizerAudioPeakLevel = 3;
        TaskbarVisualizerSmoothing = 70;
        TaskbarVisualizerBaselineAutoHide = true;
        TaskbarVisualizerHighRefreshRate = true;
        VolumeControlEnabled = false;
        VolumeControlAboveMediaFlyout = false;
        VolumeControlDuration = 3000;
        VolumeMixerEnabled = false;
        VolumeMixerHighlightActiveApps = false;
        AcrylicBlurOpacity = 175;
        UseAlbumArtAsAccentColor = false;
        AlbumAccentDesaturationThreshold = 65;
        AlbumAccentDesaturationAmount = 0;
        LegacyTaskbarWidthEnabled = false;
        AllowedApps = [];
        BlockedApps = [];

        PropertyChanged += OnPropertyChangedSaveSettings;
        PropertyChanged += OnIslandSettingChanged;
    }

    // ------------------------------------------------------------------
    // Settings that change the Island (change island-fichas)
    // ------------------------------------------------------------------

    /// <summary>
    /// The Island container, if its window already exists (while loading the settings
    /// it does not yet). Resolved once for the whole table: before, every setting
    /// method repeated the window lookup on its own.
    /// </summary>
    private static IslandWindow? Island
    {
        get
        {
            MainWindow? shell = Application.Current?.MainWindow as MainWindow;
            return shell?.islandWindow;
        }
    }

    /// <summary>
    /// Table of the settings that change the container's view: setting → what to redo.
    /// Before there were forty-eight partial methods, one per setting, each with its
    /// its own call to the window; adding a setting meant two edits spread across the
    /// whole file and forgetting one left the setting with no live effect. Now it is
    /// one line here, and the compiler writes the setting's name (<c>nameof</c>), so a
    /// nombre mal escrito no compila.
    /// </summary>
    private static readonly Dictionary<string, Action<IslandWindow>> IslandSettingHandlers = new(StringComparer.Ordinal)
    {
        // Turning the container on: it folds back or returns, depending on the setting.
        [nameof(IslandEnabled)] = island => island.RefreshEnabledState(),

        // Appearance: shape, radii, typography and padding of the compact and expanded views.
        [nameof(IslandBorderEnabled)] = island => island.RefreshAppearance(),
        [nameof(IslandBorderRadius)] = island => island.RefreshAppearance(),
        [nameof(IslandCompactBorderRadius)] = island => island.RefreshAppearance(),
        [nameof(IslandExpandedBorderRadius)] = island => island.RefreshAppearance(),
        [nameof(IslandNotchFilletCompact)] = island => island.RefreshAppearance(),
        [nameof(IslandNotchFilletExpanded)] = island => island.RefreshAppearance(),
        [nameof(IslandAlbumArtRadius)] = island => island.RefreshAppearance(),
        [nameof(IslandExpandedWidth)] = island => island.RefreshAppearance(),
        [nameof(IslandExpandedHeight)] = island => island.RefreshAppearance(),
        [nameof(IslandActivityLine)] = island => island.RefreshAppearance(),
        [nameof(IslandHiddenAccess)] = island => island.RefreshAppearance(),
        // Rest (piece or nothing): it is about WHEN it peeks out, not how it looks, so it
        // re-resolves the view instead of just repainting it (the switch only took
        // effect on the next reconciliation).
        [nameof(IslandReturnToInactive)] = island => island.RefreshVisibilityState(),
        [nameof(IslandStyle)] = island => island.RefreshAppearance(),
        [nameof(IslandAnimated)] = island => island.RefreshAppearance(),
        [nameof(FlyoutAnimationSpeed)] = island => island.RefreshAppearance(),
        [nameof(IslandLineTopOffset)] = island => island.RefreshAppearance(),
        [nameof(IslandTopOffset)] = island => island.RefreshAppearance(),
        [nameof(IslandFontFamily)] = island => island.RefreshAppearance(),
        [nameof(IslandTextStyle)] = island => island.RefreshAppearance(),
        [nameof(IslandCompactTitleFontSize)] = island => island.RefreshAppearance(),
        [nameof(IslandExpandedTitleFontSize)] = island => island.RefreshAppearance(),
        [nameof(IslandExpandedArtistFontSize)] = island => island.RefreshAppearance(),
        [nameof(IslandTimerShowProgress)] = island => island.RefreshAppearance(),
        [nameof(IslandTimerShowArrows)] = island => island.RefreshAppearance(),
        // Equalizer: its presence and its start/stop are decided by the CONTAINER (not the
        // visualizer, which reads the rest of the parameters live): without this the
        // switch only took effect on the next reconciliation - it could take until
        // the next media event.
        [nameof(IslandEqEnabled)] = island => island.RefreshEqContent(),
        [nameof(IslandEqBarCount)] = island => island.RefreshEqContent(),
        // Mouse strip: the hook reads the tolerance live, but the clickable strip and the
        // fold-back veto measure with the applied geometry; it has to be repainted.
        [nameof(IslandHoverToleranceHorizontal)] = island => island.RefreshAppearance(),
        [nameof(IslandHoverToleranceVertical)] = island => island.RefreshAppearance(),

        // When and how it peeks out: the visibility contract is recomputed on the spot.
        [nameof(IslandShowOnPlayPause)] = island => island.RefreshVisibilityState(),
        [nameof(IslandShowOnPause)] = island => island.RefreshVisibilityState(),
        [nameof(IslandVisibilityMode)] = island => island.RefreshVisibilityState(),
        [nameof(IslandPauseCountsActive)] = island => island.RefreshVisibilityState(),

        // Background: mode (blur, rotation or color), spin and spin speed.
        [nameof(IslandBackgroundBlur)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundBlurIntensity)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundBlurRadius)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundRotate)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundRotateSide)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundRotateDirection)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundRotateDuration)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundRotateSize)] = island => island.UpdateBackgroundMode(),
        [nameof(IslandBackgroundRotateHighRefreshRate)] = island => island.RefreshBackgroundRotationFrameRate(),

        // Ultra mode: it moves the middle out of the compact view and re-measures the capsule.
        [nameof(IslandUltraCompact)] = island => island.RefreshUltraCompact(),

        // A pantalla completa, apartarse.
        [nameof(IslandHideOnFullscreen)] = island => island.RefreshSuppressionState(),

        // Turning a feature on or off: the container re-hooks its views and the service
        // starts or stops (off means nothing is observed and nothing goes to the network).
        [nameof(IslandMediaEnabled)] = island => island.RefreshMediaContent(),
        [nameof(IslandTimerEnabled)] = island => island.RefreshTimerEnabled(),
        [nameof(IslandAppsEnabled)] = island => island.RefreshAppsContent(),
        [nameof(IslandShelfEnabled)] = island => island.RefreshShelfContent(),
        [nameof(IslandCalendarEnabled)] = island => island.RefreshCalendarContent(),
        [nameof(IslandBluetoothEnabled)] = island => island.RefreshBluetoothContent(),
        [nameof(IslandClipboardEnabled)] = island => island.RefreshClipboardContent(),
        [nameof(IslandClipboardMaxItems)] = island => island.RefreshClipboardContent(),
        [nameof(IslandWeatherEnabled)] = island => island.RefreshWeatherContent(),
        [nameof(IslandWeatherPlace)] = island => island.RefreshWeatherContent(),
        [nameof(IslandPowerEnabled)] = island => island.RefreshPowerContent(),
        [nameof(DictationEnabled)] = island => island.RefreshDictationContent(),
    };

    /// <summary>
    /// Applies live the setting that just changed, if the view depends on it. A
    /// setting that is also SANITIZED (clamped or rounded) has its own partial
    /// method, which calls the window after sanitizing.
    /// </summary>
    private void OnIslandSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_initializing || e.PropertyName is not { } name) return;
        if (!IslandSettingHandlers.TryGetValue(name, out var apply)) return;
        if (Island is { } island) apply(island);
    }

    [XmlIgnore]
    private CancellationTokenSource? _saveSettingsCts;

    private async void OnPropertyChangedSaveSettings(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_initializing) return;

        // Only trigger save if a persisted property changed
        if (string.IsNullOrEmpty(e.PropertyName) || !PersistedPropertyNames.Contains(e.PropertyName))
            return;

#if DEBUG
        Logger.Debug("Property '{PropertyName}' changed, scheduling settings save.", e.PropertyName);
#endif

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _saveSettingsCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await Task.Delay(500, newCts.Token);

            if (ReferenceEquals(_saveSettingsCts, newCts))
            {
                SettingsManager.SaveSettings();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when replaced by a new property change
#if DEBUG
            Logger.Debug("Settings save canceled due to another property change.");
#endif
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "An error occurred while saving settings from property change.");
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _saveSettingsCts, null, newCts) == newCts)
            {
                newCts.Dispose();
            }
        }
    }

    /// <summary>
    /// Called after deserialization to finalize initialization
    /// </summary>
    internal void CompleteInitialization()
    {
        // Migration of the legacy single radius: old XML files do not bring the new
        // radii (-1), so they inherit the value the user already had.
        if (IslandCompactBorderRadius < 0)
            IslandCompactBorderRadius = Math.Clamp(IslandBorderRadius, 0, 40);
        if (IslandExpandedBorderRadius < 0)
            IslandExpandedBorderRadius = Math.Clamp(IslandBorderRadius, 0, 40);
        if (IslandNotchFilletCompact < 0)
            IslandNotchFilletCompact = 6;
        if (IslandNotchFilletExpanded < 0)
            IslandNotchFilletExpanded = 10;
        if (IslandHoverToleranceHorizontal < 0)
            IslandHoverToleranceHorizontal = Math.Clamp(IslandHoverTolerance, 4, 30);
        if (IslandHoverToleranceVertical < 0)
            IslandHoverToleranceVertical = Math.Clamp(IslandHoverTolerance, 4, 30);
        // defaults sensatos si viene de 0 legacy mal migrado
        if (IslandHoverToleranceHorizontal < 0) IslandHoverToleranceHorizontal = 12;
        if (IslandHoverToleranceVertical < 0) IslandHoverToleranceVertical = 4;
        // Preset migration: old XML without the collection, with duplicated presets
        // (previous startups) or with invalid data.
        IslandTimerPresets ??= [];
        SanitizeTimerPresets();
        // Tray migration: old XML without the collection, or with repeated entries,
        // without a name, or above the maximum.
        IslandApps ??= [];
        SanitizeIslandApps();
        /// Shelf migration: old XML without the collection (or with elements whose
        // path no longer exists: they were taken out with the app closed) start clean.
        IslandShelfItems ??= [];
        SanitizeIslandShelf();
        // Screen migration: old XML without the setting starts with one screen per feature
        // (the usual behaviour) and any empty screen, with unknown ids, or with more
        // features than fit is sanitized.
        IslandScreens ??= [.. IslandFeatureIds.DefaultScreens];
        SanitizeIslandScreens();
        // Calendar migration: old XML without these settings starts with the feature off
        // (nobody grants access to their calendar by surprise) and with the default
        // values. Strings are never null after the XML.
        GoogleCalendarClientId ??= "";
        GoogleCalendarClientSecret ??= "";
        GoogleCalendarAccessToken ??= "";
        GoogleCalendarRefreshToken ??= "";
        GoogleCalendarAccount ??= "";
        GoogleCalendarError = "";
        GoogleCalendarStatus = "";
        GoogleCalendarReminderMinutes = Math.Clamp(GoogleCalendarReminderMinutes, 1, 60);
        GoogleCalendarRefreshMinutes = Math.Clamp(GoogleCalendarRefreshMinutes, 1, 60);
        GoogleCalendarDaysAhead = Math.Clamp(GoogleCalendarDaysAhead, 1, 14);
        _initializing = false;
    }

    private static ObservableCollection<TimerPreset> DefaultTimerPresets() =>
    [
        new TimerPreset { Name = "1 minuto", DurationSeconds = 60 },
        new TimerPreset { Name = "5 minutos", DurationSeconds = 300 },
        new TimerPreset { Name = "15 minutos", DurationSeconds = 900 },
        new TimerPreset { Name = "25 minutos", DurationSeconds = 1500 },
    ];

    private void SanitizeTimerPresets()
    {
        // Self-repair of exact duplicates: files saved by the versions that seeded the
        // collection in the constructor carry the list repeated (4+4+2...). The first
        // occurrence of each name+duration pair is kept and the rest discarded.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in IslandTimerPresets.ToList())
        {
            string key = $"{preset.Name.Trim()}\n{preset.DurationSeconds}";
            if (!seen.Add(key)) IslandTimerPresets.Remove(preset);
        }
        if (IslandTimerPresets.Count == 0) IslandTimerPresets = DefaultTimerPresets();
        if (IslandTimerPresets.Count > Classes.IslandTimer.MaxPresets)
        {
            foreach (var extra in IslandTimerPresets.Skip(Classes.IslandTimer.MaxPresets).ToList())
                IslandTimerPresets.Remove(extra);
        }
        int i = 1;
        foreach (var preset in IslandTimerPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = $"Preset {i}";
            i++;
        }
    }

    partial void OnIslandTimerPresetsChanged(ObservableCollection<TimerPreset> oldValue, ObservableCollection<TimerPreset> newValue)
    {
        if (oldValue != null)
        {
            oldValue.CollectionChanged -= TimerPresets_CollectionChanged;
            foreach (var item in oldValue) item.PropertyChanged -= TimerPreset_PropertyChanged;
        }
        if (newValue != null)
        {
            newValue.CollectionChanged += TimerPresets_CollectionChanged;
            foreach (var item in newValue) item.PropertyChanged += TimerPreset_PropertyChanged;
        }
    }

    private void TimerPresets_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (TimerPreset item in e.OldItems) item.PropertyChanged -= TimerPreset_PropertyChanged;
        if (e.NewItems != null)
            foreach (TimerPreset item in e.NewItems) item.PropertyChanged += TimerPreset_PropertyChanged;
        if (!_initializing) SettingsManager.SaveSettings();
    }

    private void TimerPreset_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initializing && e.PropertyName is nameof(TimerPreset.Name) or nameof(TimerPreset.DurationSeconds))
            SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Creates a preset (max. <see cref="IslandTimer.MaxPresets"/>). Used by IslandPage.
    /// </summary>
    public bool AddTimerPreset()
    {
        if (IslandTimerPresets.Count >= Classes.IslandTimer.MaxPresets)
        {
            TimerPresetsError = IslandStrings.Format("IslandPresetsMax",
                "Max {0} presets. Delete one to create another.", Classes.IslandTimer.MaxPresets);
            return false;
        }
        IslandTimerPresets.Add(new TimerPreset { Name = $"Preset {IslandTimerPresets.Count + 1}", DurationSeconds = 300 });
        TimerPresetsError = "";
        return true;
    }

    /// <summary>
    /// Deletes a preset. The running countdown does not change (spec 002, edge cases).
    /// </summary>
    public void RemoveTimerPreset(TimerPreset preset)
    {
        IslandTimerPresets.Remove(preset);
        TimerPresetsError = "";
    }

    /// <summary>
    /// Tray self-repair: empty or repeated paths out, name taken from the executable
    /// when missing, and trimming above the maximum.
    /// </summary>
    private void SanitizeIslandApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in IslandApps.ToList())
        {
            string path = (app.Path ?? "").Trim();
            if (path.Length == 0 || !seen.Add(path)) { IslandApps.Remove(app); continue; }
            if (string.IsNullOrWhiteSpace(app.Name)) app.Name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        if (IslandApps.Count > IslandApp.MaxApps)
        {
            foreach (var extra in IslandApps.Skip(IslandApp.MaxApps).ToList())
                IslandApps.Remove(extra);
        }
    }

    partial void OnIslandAppsChanged(ObservableCollection<IslandApp> oldValue, ObservableCollection<IslandApp> newValue)
    {
        if (oldValue != null)
        {
            oldValue.CollectionChanged -= IslandApps_CollectionChanged;
            foreach (var item in oldValue) item.PropertyChanged -= IslandApp_PropertyChanged;
        }
        if (newValue != null)
        {
            newValue.CollectionChanged += IslandApps_CollectionChanged;
            foreach (var item in newValue) item.PropertyChanged += IslandApp_PropertyChanged;
        }
    }

    private void IslandApps_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (IslandApp item in e.OldItems) item.PropertyChanged -= IslandApp_PropertyChanged;
        if (e.NewItems != null)
            foreach (IslandApp item in e.NewItems) item.PropertyChanged += IslandApp_PropertyChanged;
        if (!_initializing) SettingsManager.SaveSettings();
        // The container needs to know the tray's availability changed
        // (with no apps there is nothing to show).
        Island?.RefreshAppsContent();
    }

    private void IslandApp_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initializing && e.PropertyName is nameof(IslandApp.Name)) SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Adds an app to the Island tray (max. 12, no repeated path).
    /// The name comes from the executable's description. Used by IslandPage.
    /// </summary>
    public bool AddIslandApp(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0)
        {
            IslandAppsError = IslandStrings.Get("IslandAppsPick", "Pick an app to add.");
            return false;
        }
        if (IslandApps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            IslandAppsError = IslandStrings.Get("IslandAppsDuplicate", "That app is already in the drawer.");
            return false;
        }
        if (IslandApps.Count >= IslandApp.MaxApps)
        {
            IslandAppsError = IslandStrings.Format("IslandAppsMax",
                "Max {0} apps. Remove one to add another.", IslandApp.MaxApps);
            return false;
        }
        string name = "";
        try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; }
        catch { }
        if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
        IslandApps.Add(new IslandApp { Name = name, Path = path });
        IslandAppsError = "";
        return true;
    }

    /// <summary>
    /// Removes an app from the tray. The already open app is not closed.
    /// </summary>
    public void RemoveIslandApp(IslandApp app)
    {
        IslandApps.Remove(app);
        IslandAppsError = "";
    }

    /// <summary>
    /// Sanitizes the Island screens: the factory header injected by the startup bug
    /// is removed, duplicates are discarded, each screen is rewritten with its known
    /// features and no repeats, and the ones left with none are discarded. A screen
    /// with more features than fit is SPLIT across screens of
    /// <see cref="IslandFeatureIds.MaxFeaturesPerScreen"/> (nothing is lost when
    /// loading an old or hand-edited setting). With no valid screen the factory ones
    /// apply (never an empty navigation).
    ///
    /// <para>The NOTICES (<see cref="IslandFeatureIds.Notices"/>) leave here: they are
    /// not screens. An old setting that had them in one (it was the "Bluetooth and
    /// charger" screen nobody asked for) is left without it, and its notices keep
    /// working as before, because they no longer depend on any screen.</para>
    /// </summary>
    internal void SanitizeIslandScreens()
    {
        // Self-repair of the startup bug: that version seeded the factory screens in the
        // constructor and the XML deserializer ADDS to the existing collection, so
        // files from back then start with the WHOLE factory list, IN ORDER, ahead of
        // the user's screens (once per startup). Nobody chose that header: it is
        // removed when there is something behind it. With only the factory list it is
        // left alone: whoever never edited their screens keeps their Island as it was.
        int header = LegacyFactoryScreensHeaderLength();
        if (header > 0 && header < IslandScreens.Count)
        {
            for (int at = 0; at < header; at++) IslandScreens.RemoveAt(0);
        }
        var cleaned = new List<string>();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        // An EXACT repeated screen is not a user decision: nobody wants the same view
        // twice in a row, and that was exactly what the bug left behind.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in IslandScreens.ToList())
        {
            // A feature can only belong to one screen. The first occurrence is kept to
            // preserve the order the user already had, and later occurrences are
            // removed, even if the full screen string was not identical.
            var ids = IslandFeatureIds.ParseScreen(screen)
                .Where(assigned.Add)
                .ToList();
            for (int at = 0; at < ids.Count; at += IslandFeatureIds.MaxFeaturesPerScreen)
            {
                string normalized = IslandFeatureIds.FormatScreen(
                    ids.Skip(at).Take(IslandFeatureIds.MaxFeaturesPerScreen));
                if (normalized.Length > 0 && seen.Add(normalized)) cleaned.Add(normalized);
            }
        }
        IslandScreens.Clear();
        foreach (var screen in cleaned) IslandScreens.Add(screen);
        if (IslandScreens.Count == 0)
        {
            foreach (var screen in IslandFeatureIds.DefaultScreens) IslandScreens.Add(screen);
        }
    }

    /// <summary>
    /// How many INITIAL entries are exactly the OLD factory list (one screen per
    /// feature, in <see cref="IslandFeatureIds.All"/> order), in order: 0 if the file
    /// does not start with them. It recognizes the header the startup bug left,
    /// repeated as many times as the file went through startups. It is an exact
    /// fingerprint: one screen per feature, all of them, and in that order.
    /// </summary>
    private int LegacyFactoryScreensHeaderLength()
    {
        var legacy = IslandFeatureIds.All;
        int length = 0;
        while (length + legacy.Count <= IslandScreens.Count)
        {
            bool matches = true;
            for (int at = 0; at < legacy.Count && matches; at++)
                matches = string.Equals(IslandScreens[length + at], legacy[at], StringComparison.Ordinal);
            if (!matches) break;
            length += legacy.Count;
        }
        return length;
    }

    /// <summary>
    /// Screens added/removed: when the collection is replaced, the save and the
    /// container are re-hooked.
    /// </summary>
    partial void OnIslandScreensChanged(ObservableCollection<string> oldValue, ObservableCollection<string> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= IslandScreens_CollectionChanged;
        if (newValue != null) newValue.CollectionChanged += IslandScreens_CollectionChanged;
    }

    private void IslandScreens_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_initializing) SettingsManager.SaveSettings();
        // The container navigates these screens: they are re-read on the spot and, if the
        // Island is on screen, the current screen is presented again with the new
        // composition, without restarting the app (change island-pantallas RF-2).
        Island?.RefreshScreensContent();
    }

    // --- Island screens (change island-pantallas) ---

    /// <summary>Moves a screen in the list (container navigation order).</summary>
    internal void MoveIslandScreen(string screen, int delta)
    {
        int from = IslandScreens.IndexOf(screen);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= IslandScreens.Count) return;
        IslandScreens.Move(from, to);
    }

    /// <summary>
    /// Adds a feature to the END of a screen. The order inside a screen is the order
    /// its features are presented in (left to right: the expanded columns, and the
    /// compact's turn), so the new one enters at the end and the user places it with
    /// <see cref="MoveIslandScreenFeature"/>. A full screen
    /// (<see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>) takes no more: that is the
    /// maximum that fits in a single Island screen.
    /// </summary>
    internal bool AddIslandScreenFeature(string screen, string featureId)
    {
        if (!IslandFeatureIds.IsScreenable(featureId)) return false;
        int index = IslandScreens.IndexOf(screen);
        if (index < 0) return false;
        var ids = IslandFeatureIds.ParseScreen(screen).ToList();
        if (ids.Count >= IslandFeatureIds.MaxFeaturesPerScreen) return false;
        if (ids.Contains(featureId)) return false;
        for (int at = 0; at < IslandScreens.Count; at++)
        {
            if (at != index && IslandFeatureIds.ParseScreen(IslandScreens[at]).Contains(featureId))
                return false;
        }
        ids.Add(featureId);
        IslandScreens[index] = IslandFeatureIds.FormatScreen(ids);
        return true;
    }

    /// <summary>
    /// Moves a feature within its screen (presentation order).
    /// </summary>
    internal bool MoveIslandScreenFeature(string screen, string featureId, int delta)
    {
        int index = IslandScreens.IndexOf(screen);
        if (index < 0) return false;
        var ids = IslandFeatureIds.ParseScreen(screen).ToList();
        int from = ids.IndexOf(featureId);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= ids.Count) return false;
        (ids[from], ids[to]) = (ids[to], ids[from]);
        IslandScreens[index] = IslandFeatureIds.FormatScreen(ids);
        return true;
    }

    /// <summary>
    /// Removes a feature from its screen. The last one cannot be removed: a screen
    /// with no features would have nothing to show (it does not exist).
    /// </summary>
    internal bool RemoveIslandScreenFeature(string screen, string featureId)
    {
        int index = IslandScreens.IndexOf(screen);
        if (index < 0) return false;
        var ids = IslandFeatureIds.ParseScreen(screen).ToList();
        if (ids.Count <= 1 || !ids.Remove(featureId)) return false;
        IslandScreens[index] = IslandFeatureIds.FormatScreen(ids);
        return true;
    }

    /// <summary>
    /// Adds a new screen. It is born with the first feature that is in no screen; if
    /// they are all already spread out it does not create a duplicate screen.
    /// </summary>
    internal bool AddIslandScreen()
    {
        string? id = IslandFeatureIds.Screenable.FirstOrDefault(candidate =>
            !IslandScreens.Any(screen => IslandFeatureIds.ParseScreen(screen).Contains(candidate)));
        if (id == null) return false;
        IslandScreens.Add(id);
        return true;
    }

    /// <summary>
    /// Whether a screen can still be created without duplicating a feature.
    /// The notices do not count: they are not screens (if they did, the "New screen"
    /// button would offer to create one for Bluetooth or the charger, which have
    /// nothing to open).
    /// </summary>
    internal bool HasUnassignedIslandFeature() => IslandFeatureIds.Screenable.Any(candidate =>
        !IslandScreens.Any(screen => IslandFeatureIds.ParseScreen(screen).Contains(candidate)));

    /// <summary>Removes a screen; the last one cannot be removed (empty navigation).</summary>
    internal void RemoveIslandScreen(string screen)
    {
        if (IslandScreens.Count <= 1) return;
        IslandScreens.Remove(screen);
    }

    // --- Island file shelf ---

    /// <summary>
    /// The shelf's own folder: files and folders dropped on the Island are MOVED
    /// here. It lives next to the settings, in %APPDATA%\FluentFlyout\IslandShelf,
    /// and it is the folder the settings' "Open folder" button opens.
    /// </summary>
    public static string IslandShelfFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "IslandShelf");

    /// <summary>
    /// Shelf self-repair: out go the items whose path no longer exists (the user took
    /// them out with the app closed) and trimming above the maximum.
    /// It never touches the disk: the shelf does not delete files.
    /// </summary>
    private void SanitizeIslandShelf()
    {
        foreach (var item in IslandShelfItems.ToList())
            if (!item.Exists) IslandShelfItems.Remove(item);
        if (IslandShelfItems.Count > IslandShelfItem.MaxItems)
        {
            foreach (var extra in IslandShelfItems.Skip(IslandShelfItem.MaxItems).ToList())
                IslandShelfItems.Remove(extra);
        }
    }

    partial void OnIslandShelfItemsChanged(ObservableCollection<IslandShelfItem> oldValue, ObservableCollection<IslandShelfItem> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= IslandShelfItems_CollectionChanged;
        if (newValue != null) newValue.CollectionChanged += IslandShelfItems_CollectionChanged;
    }

    private void IslandShelfItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_initializing) SettingsManager.SaveSettings();
        // The container fills its lists (and, with the shelf on screen, keeps it up to date).
        Island?.RefreshShelfContent();
    }

    // --- Google Calendar ---

    /// <summary>
    /// Signs out of Google: the tokens are erased and with them the ability to read
    /// the calendar. The in-memory events are dropped by the service when it
    /// reconfigures (nothing of someone else's calendar is painted after signing
    /// out). The OAuth client settings are kept so they do not have to be pasted again.
    /// </summary>
    public void SignOutGoogleCalendar()
    {
        GoogleCalendarAccessToken = "";
        GoogleCalendarRefreshToken = "";
        GoogleCalendarTokenExpiresUtc = DateTime.MinValue;
        GoogleCalendarAccount = "";
        GoogleCalendarError = "";
        GoogleCalendarStatus = IslandStrings.Get("IslandCalendarSignedOut", "Signed out.");
        GoogleCalendarService.Instance.Configure();
    }

    /// <summary>
    /// Drops files and folders onto the shelf: each one is MOVED into the shelf folder
    /// - a free name if theirs is taken: nothing is overwritten here - and where it
    /// came from is recorded, so it can be returned on removal. Returns how many
    /// went in.
    /// </summary>
    public int AddIslandShelfPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string? raw in paths)
        {
            string source = (raw ?? "").Trim();
            if (source.Length == 0) continue;
            // Already on the shelf: it is not duplicated.
            if (IslandShelfItems.Any(i => string.Equals(i.Path, source, StringComparison.OrdinalIgnoreCase))) continue;
            if (IslandShelfItems.Count >= IslandShelfItem.MaxItems)
            {
                IslandShelfError = IslandStrings.Format("IslandShelfMax",
                    "Max {0} items on the shelf.", IslandShelfItem.MaxItems);
                break;
            }
            bool isFolder = Directory.Exists(source);
            if (!isFolder && !File.Exists(source)) continue;
            if (!TryEnsureShelfFolder()) break;
            string? parked = MoveTo(IslandShelfFolder, source, isFolder);
            if (parked == null)
            {
                IslandShelfError = IslandStrings.Get("IslandShelfParkFailed", "Could not park an item (check the log).");
                continue;
            }
            IslandShelfItems.Add(new IslandShelfItem { Path = parked, Origin = source, IsFolder = isFolder });
            added++;
        }
        if (added > 0) IslandShelfError = "";
        return added;
    }

    /// <summary>
    /// Removes an item from the shelf by returning it to its original folder: removing
    /// it must never destroy anything of the user's. If the original folder no longer
    /// exists - or the name is taken and could not be numbered - the item stays and
    /// the reason is explained. Returns true if it left the shelf.
    /// </summary>
    public bool RemoveIslandShelfItem(IslandShelfItem item)
    {
        try
        {
            string originDir = System.IO.Path.GetDirectoryName(item.Origin) ?? "";
            if (originDir.Length == 0 || !Directory.Exists(originDir))
            {
                IslandShelfError = IslandStrings.Get("IslandShelfOriginGone",
                    "The original folder is gone: the item stays on the shelf.");
                return false;
            }
            string target = FreeTarget(originDir, System.IO.Path.GetFileName(item.Origin), item.IsFolder);
            if (item.IsFolder) Directory.Move(item.Path, target);
            else File.Move(item.Path, target);
            IslandShelfItems.Remove(item);
            IslandShelfError = "";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Estante: no se pudo devolver {Path} a su carpeta original", item.Path);
            IslandShelfError = IslandStrings.Get("IslandShelfReturnFailed",
                "Could not return the item to its original folder.");
            return false;
        }
    }

    private bool TryEnsureShelfFolder()
    {
        try
        {
            Directory.CreateDirectory(IslandShelfFolder);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Estante: no se pudo preparar la carpeta {Folder}", IslandShelfFolder);
            IslandShelfError = IslandStrings.Get("IslandShelfFolderFailed",
                "Could not prepare the shelf folder.");
            return false;
        }
    }

    /// <summary>Moves a file or folder into the shelf folder, overwriting nothing.</summary>
    private static string? MoveTo(string folder, string source, bool isFolder)
    {
        try
        {
            string target = FreeTarget(folder, System.IO.Path.GetFileName(source), isFolder);
            if (isFolder) Directory.Move(source, target);
            else File.Move(source, target);
            return target;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Estante: no se pudo mover {Source} a {Folder}", source, folder);
            return null;
        }
    }

    /// <summary>
    /// First free path in <paramref name="folder"/> for that name: if it is taken it is
    /// numbered ("name (2).ext", "name (3).ext"...). It never returns a taken path,
    /// so no shelf move overwrites a file that was already there.
    /// </summary>
    private static string FreeTarget(string folder, string name, bool isFolder)
    {
        string target = System.IO.Path.Combine(folder, name);
        int n = 1;
        while (File.Exists(target) || Directory.Exists(target))
        {
            string stem = isFolder ? name : System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = isFolder ? "" : System.IO.Path.GetExtension(name);
            target = System.IO.Path.Combine(folder, $"{stem} ({++n}){ext}");
        }
        return target;
    }

    partial void OnAppLanguageChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue) return;
        SelectedLanguage = LanguageOptions.First(l => l.Tag == newValue);
    }

    partial void OnSelectedLanguageChanged(LanguageOption oldValue, LanguageOption newValue)
    {
        if (oldValue == newValue || _initializing) return;
        AppLanguage = newValue.Tag;
        LocalizationManager.ApplyLocalization();
    }

    /// <summary>
    /// Changes the application theme when the selection is changed. 0 for default, 1 for light, 2 for dark.
    /// </summary>
    partial void OnAppThemeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ThemeManager.ApplyAndSaveTheme(newValue);
    }

    partial void OnNIconSymbolChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ThemeManager.UpdateTrayIcon();
    }

    partial void OnAcrylicBlurOpacityChanged(uint oldValue, uint newValue)
    {
        if (oldValue == newValue || _initializing) return;
        WindowBlurHelper.AdjustBlurOpacityForAllWindows(newValue);
    }

    partial void OnTaskbarWidgetEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetExpandOnClickChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        if (Application.Current?.MainWindow is MainWindow mainWindow)
            mainWindow.taskbarWindow?.RefreshExpansionPreference();
    }

    // Update taskbar when relevant settings change
    partial void OnTaskbarWidgetPositionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetManualPaddingChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetBorderRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.ApplyCornerRadius();
    }

    partial void OnTaskbarWidgetAlbumArtRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.ApplyCornerRadius();
    }

    partial void OnTaskbarWidgetButtonHoverRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.ApplyButtonHoverRadius();
    }

    partial void OnTaskbarWidgetBackgroundBlurChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetBackgroundRotateChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.UpdateBackgroundMode();
    }

    partial void OnTaskbarWidgetBackgroundRotateSideChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.UpdateBackgroundMode();
    }

    partial void OnTaskbarWidgetBackgroundRotateDirectionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.UpdateBackgroundMode();
    }

    partial void OnTaskbarWidgetBackgroundRotateHighRefreshRateChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.RefreshBackgroundRotationFrameRate();
    }

    partial void OnTaskbarWidgetBackgroundRotateDurationChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.UpdateBackgroundMode();
    }

    partial void OnTaskbarWidgetBackgroundRotateSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.UpdateBackgroundMode();
    }

    partial void OnTaskbarWidgetHideCompletelyChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetFixedWidthChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetFixedWidthPxChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetShowPauseOverlayChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetShowAlbumArtChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetControlsEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetControlsPositionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.taskbarWindow?.Widget?.ReorderControls();
    }

    partial void OnTaskbarVisualizerPositionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    partial void OnLegacyTaskbarWidthEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    private void UpdateTaskbar()
    {
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.UpdateTaskbar();
    }

    partial void OnTaskbarWidgetScrollingEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbarMarquees();
    }

    partial void OnTaskbarWidgetScrollingTextLoopForeverChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbarMarquees();
    }

    partial void OnTaskbarWidgetScrollingTextSpeedChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbarMarquees();
    }

    private void UpdateTaskbarMarquees()
    {
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        var widget = mainWindow.taskbarWindow?.Widget;
        if (widget == null) return;
        widget.Dispatcher.Invoke(widget.UpdateMarquees);
    }

    partial void OnTaskbarWidgetFontFamilyChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ApplyTaskbarTextStyle();
    }

    partial void OnTaskbarWidgetTextStyleChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ApplyTaskbarTextStyle();
    }

    partial void OnTaskbarWidgetTitleFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        TaskbarWidgetTitleFontSize = Math.Clamp(newValue, 10, 18);
        ApplyTaskbarTextStyle();
    }

    partial void OnTaskbarWidgetArtistFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        TaskbarWidgetArtistFontSize = Math.Clamp(newValue, 10, 16);
        ApplyTaskbarTextStyle();
    }

    /// <summary>
    /// Restyles the widget song/artist rows live and reflows the widget width.
    /// </summary>
    private void ApplyTaskbarTextStyle()
    {
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        var widget = mainWindow.taskbarWindow?.Widget;
        if (widget == null) return;
        widget.Dispatcher.Invoke(() =>
        {
            widget.ApplyTextStyle();
            mainWindow.UpdateTaskbar();
        });
    }

    partial void OnTaskbarVisualizerEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        TaskbarVisualizerControl.OnTaskbarVisualizerEnabledChanged(newValue);
        UpdateTaskbar();
    }

    partial void OnTaskbarVisualizerBarCountChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        TaskbarVisualizerControl.ResizeBars(newValue);
    }

    partial void OnTaskbarVisualizerHighRefreshRateChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        TaskbarVisualizerControl.OnTaskbarVisualizerHighRefreshRateChanged();
    }

    partial void OnIslandCompactBorderRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandCompactBorderRadius = Math.Clamp(newValue, 0, 40);
        Island?.RefreshAppearance();
    }

    partial void OnIslandExpandedBorderRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandExpandedBorderRadius = Math.Clamp(newValue, 0, 40);
        Island?.RefreshAppearance();
    }

    partial void OnIslandNotchFilletCompactChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandNotchFilletCompact = Math.Clamp(newValue, 0, 20);
        Island?.RefreshAppearance();
    }

    partial void OnIslandNotchFilletExpandedChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandNotchFilletExpanded = Math.Clamp(newValue, 0, 20);
        Island?.RefreshAppearance();
    }

    partial void OnIslandExpandedWidthChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        int fixedValue = newValue == 0 ? 320 : Math.Clamp(newValue, 280, 600);
        if (fixedValue != newValue) IslandExpandedWidth = fixedValue;
        Island?.RefreshAppearance();
    }

    partial void OnIslandExpandedHeightChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        int fixedValue = newValue == 0 ? 126 : Math.Clamp(newValue, 100, 220);
        if (fixedValue != newValue) IslandExpandedHeight = fixedValue;
        Island?.RefreshAppearance();
    }

    partial void OnIslandVisibilityModeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandVisibilityMode = Math.Clamp(newValue, 0, 1);
        Island?.RefreshVisibilityState();
    }

    partial void OnIslandLineTopOffsetChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandLineTopOffset = Math.Clamp(newValue, 0, 60);
        Island?.RefreshAppearance();
    }

    partial void OnIslandTopOffsetChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandTopOffset = Math.Clamp(newValue, 0, 80);
        Island?.RefreshAppearance();
    }

    partial void OnIslandCompactTitleFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandCompactTitleFontSize = Math.Clamp(newValue, 10, 24);
        Island?.RefreshAppearance();
    }

    partial void OnIslandExpandedTitleFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandExpandedTitleFontSize = Math.Clamp(newValue, 10, 24);
        Island?.RefreshAppearance();
    }

    partial void OnIslandExpandedArtistFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandExpandedArtistFontSize = Math.Clamp(newValue, 10, 24);
        Island?.RefreshAppearance();
    }

    partial void OnTaskbarVisualizerBaselineChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || newValue == false) return;
        TaskbarVisualizerHasContent = true;
    }

    partial void OnTaskbarVisualizerBaselineAutoHideChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        // If newValue is true, refresh the visualizer by hiding it:
        // if audio is playing, it will be shown again, if not, it will remain hidden.
        TaskbarVisualizerHasContent = !newValue;
    }

    partial void OnUseAlbumArtAsAccentColorChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        BitmapHelper.RefreshAccentTheme();
        RepaintAlbumAccent();
    }

    partial void OnAlbumAccentDesaturationThresholdChanged(uint oldValue, uint newValue)
    {
        if (oldValue == newValue || _initializing) return;
        BitmapHelper.RefreshAccentTheme();
        RepaintAlbumAccent();
    }

    partial void OnAlbumAccentDesaturationAmountChanged(uint oldValue, uint newValue)
    {
        if (oldValue == newValue || _initializing) return;
        BitmapHelper.RefreshAccentTheme();
        RepaintAlbumAccent();
    }

    /// <summary>
    /// Reapplies the current album accent to the flyout play button so
    /// slider drags give live feedback without waiting for the next track.
    /// </summary>
    private static void RepaintAlbumAccent()
    {
        try
        {
            if (Application.Current?.MainWindow is not MainWindow mainWindow)
                return;
            mainWindow.Dispatcher.Invoke(() =>
            {
                mainWindow.ControlPlayPause.Background = AlbumAccent.Brush;
            });
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to repaint album accent");
        }
    }

    partial void OnAppFilteringEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow?.RefreshFilteredMedia();
    }

    partial void OnAppFilteringModeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow?.RefreshFilteredMedia();
    }

    partial void OnVolumeControlEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.RefreshKeyboardHook();

        if (newValue == false)
        {
            // re-enable native volume flyout and dispose the lazy-created volume window
            VolumeMixerWindow.ShowVolumeOsd();

            mainWindow.DisposeVolumeWindow();
        }
    }

    partial void OnMediaFlyoutEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.RefreshKeyboardHook();
        // The Media Flyout toggle only affects the music pop-up window; the Island
        // follows the system sessions on its own.
    }

    partial void OnLockKeysEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.RefreshKeyboardHook();
    }

    /// <summary>
    /// Dictation (spec 006): turning it on or off installs or removes the keyboard hook
    /// - the hotkey is heard from the global hook - and updates the resource policy.
    /// </summary>
    partial void OnDictationEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.RefreshKeyboardHook();
        mainWindow.Dictation.RefreshSettings();
    }

    /// <summary>Changing model releases the previous one and applies the policy to the new one.</summary>
    partial void OnDictationModelChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.Dictation.RefreshAccelerationSettings();
    }

    /// <summary>
    /// Updates the legacy default; explicit per-model device choices take precedence.
    /// </summary>
    partial void OnDictationUseGpuChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.Dictation.RefreshAccelerationSettings();
    }

    partial void OnDictationModelDevicesChanged(List<DictationModelDevicePreference> value)
    {
        if (_initializing) return;
        if (Application.Current?.MainWindow is MainWindow mainWindow)
            mainWindow.Dictation.RefreshAccelerationSettings();
    }

    partial void OnDictationKeepModelLoadedChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.Dictation.RefreshResourcePolicy();
    }

    partial void OnDictationUnloadDelaySecondsChanged(int oldValue, int newValue)
    {
        int fixedValue = Math.Clamp(newValue, 15, 600);
        if (fixedValue != newValue)
        {
            DictationUnloadDelaySeconds = fixedValue;
            return;
        }

        if (_initializing) return;
        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.Dictation.RefreshResourceTimeout();
    }

    /// <summary>An unreadable hotkey (hand-edited setting) must not leave dictation mute: the factory one is restored.</summary>
    partial void OnDictationHotkeyChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        if (!Models.DictationHotkey.IsValid(newValue)) DictationHotkey = Models.DictationHotkey.Default;
    }
}
