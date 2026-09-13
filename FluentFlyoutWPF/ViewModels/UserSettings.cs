// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using FluentFlyoutWPF.Windows;
using System.Collections.ObjectModel;
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
    /// Fluent Island: muestra el borde blanco translúcido de la isla.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandBorderEnabled { get; set; }

    /// <summary>
    /// Radio de las esquinas del Fluent Island en píxeles.
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
            else IslandBorderRadius = 17;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Radio de las esquinas de la carátula y de su overlay de cambio de medio.
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
            else IslandAlbumArtRadius = 12;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Ancho en píxeles del Island expandido.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandExpandedWidthText))]
    public partial int IslandExpandedWidth { get; set; }

    [XmlIgnore]
    public string IslandExpandedWidthText
    {
        get => (IslandExpandedWidth > 0 ? Math.Clamp(IslandExpandedWidth, 280, 600) : 360).ToString();
        set
        {
            IslandExpandedWidth = int.TryParse(value, out var result)
                ? Math.Clamp(result, 280, 600)
                : 360;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Alto en píxeles del Island expandido cuando usa el estilo de isla flotante.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandExpandedHeightText))]
    public partial int IslandExpandedHeight { get; set; }

    [XmlIgnore]
    public string IslandExpandedHeightText
    {
        get => (IslandExpandedHeight > 0 ? Math.Clamp(IslandExpandedHeight, 100, 220) : 126).ToString();
        set
        {
            IslandExpandedHeight = int.TryParse(value, out var result)
                ? Math.Clamp(result, 100, 220)
                : 126;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Fluent Island: activa el fondo desenfocado basado en la carátula.
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
    /// Fluent Island: gira continuamente el fondo desenfocado.
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
    /// Fluent Island: 0 = visible mientras suena, 1 = aviso temporal (N s al reproducir).
    /// </summary>
    [ObservableProperty]
    public partial int IslandVisibilityMode { get; set; }

    /// <summary>
    /// Fluent Island: duración del aviso temporal en ms (1000..10000).
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
    /// Fluent Island: línea gris que indica que el Island está activo.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandActivityLine { get; set; }

    /// <summary>
    /// Fluent Island: 0 = isla flotante, 1 = notch superior (sale del borde de arriba).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandFloatingStyleEnabled))]
    public partial int IslandStyle { get; set; }

    /// <summary>
    /// Fluent Island: alto en px de la franja invisible que detecta el ratón (4-30).
    /// </summary>
    [ObservableProperty]
    public partial int IslandHoverTolerance { get; set; }

    [XmlIgnore]
    public bool IslandFloatingStyleEnabled => IslandEnabled && IslandStyle == 0;

    /// <summary>
    /// Fluent Island: ecualizador funcional (audio real).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandEqEnabled { get; set; }

    /// <summary>
    /// Fluent Island: mirrors the equalizer bars around the center line.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandEqCenteredBars { get; set; }

    /// <summary>
    /// Fluent Island: número de barras del ecualizador (1-10).
    /// </summary>
    [ObservableProperty]
    public partial int IslandEqBarCount { get; set; }

    /// <summary>
    /// Fluent Island: sensibilidad del ecualizador (1-3).
    /// </summary>
    [ObservableProperty]
    public partial int IslandEqSensitivity { get; set; }

    /// <summary>
    /// Fluent Island: suavizado del ecualizador (0-100).
    /// </summary>
    [ObservableProperty]
    public partial int IslandEqSmoothing { get; set; }

    /// <summary>
    /// Fluent Island: animaciones (aparición y morph).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandAnimated { get; set; }

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
        TaskbarWidgetEnabled = false;
        TaskbarWidgetSelectedMonitor = 0;
        TaskbarWidgetPosition = 0;
        TaskbarWidgetPadding = true;
        TaskbarWidgetManualPadding = 0;
        TaskbarWidgetBorderRadius = 6;
        TaskbarWidgetAlbumArtRadius = 5;
        TaskbarWidgetButtonHoverRadius = 6;
        TaskbarWidgetBackgroundBlur = false;
        TaskbarWidgetBackgroundBlurIntensity = 65;
        TaskbarWidgetBackgroundBlurRadius = 35;
        TaskbarWidgetBackgroundRotate = false;
        TaskbarWidgetBackgroundRotateSide = 0;
        TaskbarWidgetBackgroundRotateDirection = 0;
        TaskbarWidgetBackgroundRotateHighRefreshRate = false;
        TaskbarWidgetBackgroundRotateDuration = 20;
        TaskbarWidgetBackgroundRotateSize = 300;
        TaskbarWidgetHideCompletely = false;
        TaskbarWidgetClickOpensFlyout = true;
        TaskbarWidgetFixedWidth = false;
        TaskbarWidgetFixedWidthPx = 216;
        TaskbarWidgetShowAlbumArt = true;
        TaskbarWidgetShowPauseOverlay = true;
        TaskbarWidgetControlsEnabled = false;
        TaskbarWidgetControlsPosition = 1;
        TaskbarWidgetAnimated = true;
        TaskbarWidgetSongChangeAnimation = 0;
        TaskbarWidgetResizeAnimated = true;
        TaskbarWidgetFontFamily = "Segoe UI Variable";
        TaskbarWidgetTextStyle = 0;
        TaskbarWidgetTitleFontSize = 13;
        TaskbarWidgetArtistFontSize = 12;
        TaskbarWidgetScrollingEnabled = false;
        TaskbarWidgetScrollingTextSpeed = 20;
        TaskbarWidgetScrollingTextLoopForever = false;
        TaskbarVisualizerEnabled = false;
        IslandEnabled = true;
        IslandBorderEnabled = true;
        IslandBorderRadius = 17;
        IslandAlbumArtRadius = 12;
        IslandExpandedWidth = 360;
        IslandExpandedHeight = 126;
        IslandBackgroundBlur = false;
        IslandBackgroundBlurIntensity = 65;
        IslandBackgroundBlurRadius = 35;
        IslandBackgroundRotate = false;
        IslandBackgroundRotateSide = 0;
        IslandBackgroundRotateDirection = 0;
        IslandBackgroundRotateHighRefreshRate = false;
        IslandBackgroundRotateDuration = 20;
        IslandBackgroundRotateSize = 300;
        IslandStyle = 0;
        IslandHoverTolerance = 12;
        IslandVisibilityMode = 0;
        IslandVisibilityDuration = 4000;
        IslandShowOnPlayPause = true;
        IslandShowOnPause = true;
        IslandShowOnTrackChange = true;
        IslandActivityLine = false;
        IslandEqEnabled = true;
        IslandEqCenteredBars = false;
        IslandEqBarCount = 5;
        IslandEqSensitivity = 2;
        IslandEqSmoothing = 50;
        IslandAnimated = true;
        AppFilteringEnabled = false;
        AppFilteringMode = 0;
        TaskbarVisualizerPosition = 1;
        TaskbarVisualizerClickable = true;
        TaskbarVisualizerBarCount = 10;
        TaskbarVisualizerCenteredBars = false;
        TaskbarVisualizerBaseline = false;
        TaskbarVisualizerAudioSensitivity = 2;
        TaskbarVisualizerAudioPeakLevel = 3;
        TaskbarVisualizerSmoothing = 50;
        TaskbarVisualizerBaselineAutoHide = false;
        TaskbarVisualizerHighRefreshRate = false;
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
        _initializing = false;
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

    partial void OnIslandEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshEnabledState();
    }

    partial void OnIslandBorderEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandBorderRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandAlbumArtRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandExpandedWidthChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        int fixedValue = newValue == 0 ? 360 : Math.Clamp(newValue, 280, 600);
        if (fixedValue != newValue) IslandExpandedWidth = fixedValue;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandExpandedHeightChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        int fixedValue = newValue == 0 ? 126 : Math.Clamp(newValue, 100, 220);
        if (fixedValue != newValue) IslandExpandedHeight = fixedValue;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandActivityLineChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandShowOnPlayPauseChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshVisibilityState();
    }

    partial void OnIslandShowOnPauseChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshVisibilityState();
    }

    partial void OnIslandStyleChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandBackgroundBlurChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundBlurIntensityChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundBlurRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundRotateChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundRotateSideChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundRotateDirectionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundRotateHighRefreshRateChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshBackgroundRotationFrameRate();
    }

    partial void OnIslandBackgroundRotateDurationChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
    }

    partial void OnIslandBackgroundRotateSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.UpdateBackgroundMode();
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
        BitmapHelper.GetDominantColors();
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
    }

    partial void OnLockKeysEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.RefreshKeyboardHook();
    }
}
