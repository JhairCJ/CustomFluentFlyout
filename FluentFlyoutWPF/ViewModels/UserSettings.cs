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
            else IslandBorderRadius = 10;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Radio de las esquinas del Island en estado compacto (píxeles, 0-40).
    /// Valor -1 = sin migrar: se hereda de <see cref="IslandBorderRadius"/> al cargar.
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
    /// Radio de las esquinas del Island en estado expandido (píxeles, 0-40).
    /// Valor -1 = sin migrar: se hereda de <see cref="IslandBorderRadius"/> al cargar.
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
    /// Curva de empalme al borde en modo notch, estado compacto (píxeles, 0-20).
    /// Valor -1 = sin migrar: se usa 6 al cargar.
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
    /// Curva de empalme al borde en modo notch, estado expandido (píxeles, 0-20).
    /// Valor -1 = sin migrar: se usa 10 al cargar.
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
            else IslandAlbumArtRadius = 8;
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
    /// Fluent Island (001 MOD RF-2): 0 = «Visible mientras activo» (se muestra
    /// mientras alguna funcionalidad habilitada esté activa),
    /// 1 = «Aviso temporal» (muestra el contenido del evento 1–10 s y vuelve al
    /// reposo). El modo «Siempre en su lugar» se retiró (001 REMOVED).
    /// </summary>
    [ObservableProperty]
    public partial int IslandVisibilityMode { get; set; }

    /// <summary>
    /// Fluent Island: cómo se expande en "siempre en su lugar".
    /// 0 = clic en el medio, 1 = rueda abajo, 2 = ambos.
    /// </summary>
    [ObservableProperty]
    public partial int IslandExpandTrigger { get; set; }

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
    /// Fluent Island (001 MOD RF-2): toggle «volver a inactivo» del reposo.
    /// Activado (por defecto): al vencer el aviso o caer sin actividad queda la
    /// pieza inactiva (negra estrecha) visible. Desactivado: el island se oculta
    /// por completo (nada). Persistente entre reinicios.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandReturnToInactive { get; set; }

    /// <summary>
    /// Fluent Island (change island-ultra-compacto): modo ULTRA COMPACTO. Con la
    /// cápsula replegada solo se ven sus dos EXTREMOS —lo de la izquierda y lo de la
    /// derecha—: en el control de medios quedan la carátula y el ecualizador, sin el
    /// título ni el artista. La presencia de reposo es mínima a propósito, pensado para
    /// usarse con «volver a inactivo» APAGADO: el Island solo aparece cuando algo hay
    /// que enseñar y, como no hay pieza a la que apuntar, la franja de arriba (de la
    /// línea de actividad al borde) es la que lo trae de vuelta expandido.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandUltraCompact { get; set; }

    /// <summary>
    /// Fluent Island (001 MOD RF-6): ajuste «pausa de media cuenta como activo».
    /// Activado (por defecto): una sesión pausada sostiene la visibilidad en
    /// «Visible mientras activo» y muestra sus controles. Desactivado: la pausa
    /// no cuenta como actividad, aunque se conserva acceso por clic.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandPauseCountsActive { get; set; }

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

    /// <summary>
    /// Fluent Island flotante: px desde el borde superior hasta la línea gris (0-60).
    /// </summary>
    [ObservableProperty]
    public partial int IslandLineTopOffset { get; set; }

    /// <summary>
    /// Fluent Island flotante: px desde el borde superior hasta la isla (0-80), estilo iPhone.
    /// </summary>
    [ObservableProperty]
    public partial int IslandTopOffset { get; set; }

    /// <summary>
    /// Fluent Island: px invisibles a cada lado de la línea que siguen detectando (0-80). 0 = solo la línea (120px).
    /// </summary>
    [ObservableProperty]
    public partial int IslandHoverToleranceHorizontal { get; set; }

    /// <summary>
    /// Fluent Island: px invisibles hacia abajo desde la línea que siguen detectando (0-40). 0 = solo sobre la línea.
    /// </summary>
    [ObservableProperty]
    public partial int IslandHoverToleranceVertical { get; set; }

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
    /// Fluent Island: familia tipográfica compartida por los 3 textos
    /// (canción compacta, canción expandida, autor expandido).
    /// Nombres incluidos (Inter, Manrope, …) funcionan en cualquier PC;
    /// cualquier otro valor se trata como fuente del sistema.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandFontSource))]
    public partial string IslandFontFamily { get; set; }

    /// <summary>
    /// Tipo de letra resuelto para bindings XAML (pack URI para incluidas).
    /// </summary>
    [XmlIgnore]
    public FontFamily IslandFontSource => WidgetFonts.Resolve(IslandFontFamily);

    /// <summary>
    /// Fluent Island: preset de estilo de texto (0 Moderno, 1 Clásico, 2 Audaz, 3 Suave).
    /// Controla grosor, opacidad del autor y cursiva; los tamaños van por separado.
    /// </summary>
    [ObservableProperty]
    public partial int IslandTextStyle { get; set; }

    /// <summary>
    /// Fluent Island: tamaño del texto de canción en island compacto (DIPs, 10-24).
    /// </summary>
    [ObservableProperty]
    public partial int IslandCompactTitleFontSize { get; set; }

    /// <summary>
    /// Fluent Island: tamaño del texto de canción en island expandido (DIPs, 10-24).
    /// </summary>
    [ObservableProperty]
    public partial int IslandExpandedTitleFontSize { get; set; }

    /// <summary>
    /// Fluent Island: tamaño del texto de autor en island expandido (DIPs, 10-24).
    /// </summary>
    [ObservableProperty]
    public partial int IslandExpandedArtistFontSize { get; set; }

    /// <summary>
    /// Contenido multimedia del Island: funcionalidad habilitada (independiente
    /// del toggle de la ventana de Media Flyout).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandMediaEnabled { get; set; }

    /// <summary>
    /// Temporizador del Island: funcionalidad habilitada.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandTimerEnabled { get; set; }

    /// <summary>
    /// Temporizador del Island: muestra el progreso en el centro del compacto.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandTimerShowProgress { get; set; }

    /// <summary>
    /// Temporizador del Island: flechas laterales para cambiar de funcionalidad.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandTimerShowArrows { get; set; }

    /// <summary>
    /// Temporizador del Island: presets editables (máx. 10). Persisten entre reinicios.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<TimerPreset> IslandTimerPresets { get; set; }

    /// <summary>
    /// Último mensaje de validación de presets en español. Vacío = sin error.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string TimerPresetsError { get; set; }

    /// <summary>
    /// Cajón de aplicaciones del Island: funcionalidad habilitada (independiente
    /// de música y temporizador).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandAppsEnabled { get; set; }

    /// <summary>
    /// Cajón de aplicaciones del Island: aplicaciones del cajón (máx. 12) con
    /// nombre y ruta. Persisten entre reinicios; el icono se lee de la ruta.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<IslandApp> IslandApps { get; set; }

    /// <summary>
    /// Último mensaje de validación del cajón en español. Vacío = sin error.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string IslandAppsError { get; set; }

    /// <summary>
    /// Pantallas del Island (change island-pantallas): cada entrada es una pantalla
    /// con las funcionalidades que se muestran JUNTAS, por su id
    /// (<see cref="IslandFeatureIds"/>) unidas por '+'. Son la ÚNICA fuente del orden y
    /// de la composición del Island: el orden de la lista es el de la navegación (rueda
    /// y flechas), el de dentro de cada pantalla es el de presentación (de izquierda a
    /// derecha) y una funcionalidad que no esté en ninguna pantalla no se muestra. Cada
    /// pantalla lleva como máximo <see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>
    /// funcionalidades. Sin nada configurado rige una pantalla por funcionalidad, que es
    /// el comportamiento histórico; se autorrepara al cargar: los ids desconocidos o
    /// repetidos salen, las pantallas vacías se descartan y lo que viniera de más se
    /// reparte en pantallas nuevas.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<string> IslandScreens { get; set; }

    /// <summary>
    /// Estante de archivos del Island: funcionalidad habilitada. Por defecto sí: el
    /// estante vacío es su estado natural (invita a soltar algo encima).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandShelfEnabled { get; set; }

    /// <summary>
    /// Estante de archivos del Island: archivos y carpetas aparcados. Persisten entre
    /// reinicios y NO caducan: solo salen cuando el usuario los arrastra fuera o los
    /// quita (y quitarlos los devuelve a su carpeta original).
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<IslandShelfItem> IslandShelfItems { get; set; }

    /// <summary>
    /// Último mensaje del estante en español. Vacío = sin error.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string IslandShelfError { get; set; }

    /// <summary>
    /// ¿El Island se aparta cuando hay algo a pantalla completa (juego, vídeo sin
    /// bordes, presentación o equipo bloqueado)? Por defecto sí: quedarse encima de
    /// un juego es justo lo que no debe pasar. El ajuste es del Island, así que
    /// apagar «ocultar si hay pantalla completa» en el sistema no se lo lleva por
    /// delante (ese sigue mandando como acompañante).
    /// </summary>
    [ObservableProperty]
    public partial bool IslandHideOnFullscreen { get; set; }

    /// <summary>
    /// Recordatorios de Google Calendar: funcionalidad habilitada. Con ella apagada no
    /// se lee el calendario (ni red ni token) y el Island no la ofrece.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandCalendarEnabled { get; set; }

    /// <summary>
    /// Dispositivos Bluetooth conectados: funcionalidad habilitada. Con ella apagada el
    /// vigía se para y el Island no la ofrece (ni avisos ni consumo). El aviso de una
    /// conexión es SIEMPRE temporal, con la duración configurada del aviso.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandBluetoothEnabled { get; set; }

    /// <summary>
    /// Portapapeles (texto e imágenes): funcionalidad habilitada. Con ella apagada el
    /// Island no escucha el portapapeles (ni copia nada a su lista) y no la ofrece.
    /// Copiar de nuevo desde la lista del Island SÍ es cosa del usuario y sigue
    /// funcionando aunque la escucha esté apagada.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandClipboardEnabled { get; set; }

    /// <summary>
    /// Portapapeles: cuántos elementos guarda la lista (1-100). Al bajarlo se
    /// descartan los más viejos; nada de lo guardado toca al usuario hasta que
    /// pulsa un elemento.
    /// </summary>
    [ObservableProperty]
    public partial int IslandClipboardMaxItems { get; set; }

    /// <summary>
    /// Clima: funcionalidad habilitada. Con ella apagada no se consulta nada a la red
    /// (ni el buscador de lugares) y el Island no la ofrece.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandWeatherEnabled { get; set; }

    /// <summary>Clima: lugar elegido, tal y como se enseña («Vigo, Galicia, España»).</summary>
    [ObservableProperty]
    public partial string IslandWeatherPlace { get; set; } = "";

    /// <summary>Clima: latitud del lugar elegido (0 = sin lugar).</summary>
    [ObservableProperty]
    public partial double IslandWeatherLatitude { get; set; }

    /// <summary>Clima: longitud del lugar elegido.</summary>
    [ObservableProperty]
    public partial double IslandWeatherLongitude { get; set; }

    /// <summary>Clima: estado del buscador/refresco, para la página de ajustes.</summary>
    [ObservableProperty]
    public partial string IslandWeatherStatus { get; set; } = "";

    /// <summary>Clima: motivo del último fallo (vacío si todo fue bien).</summary>
    [ObservableProperty]
    public partial string IslandWeatherError { get; set; } = "";

    /// <summary>
    /// Cargador: avisa en el Island cuando el equipo se enchufa a la corriente y
    /// cuando se queda a batería. Con ella apagada no se observa el estado de energía.
    /// </summary>
    [ObservableProperty]
    public partial bool IslandPowerEnabled { get; set; }


    /// <summary>
    /// Google Calendar: minutos de antelación del recordatorio (1-60). El aviso sigue
    /// vivo hasta dos minutos después del comienzo.
    /// </summary>
    [ObservableProperty]
    public partial int GoogleCalendarReminderMinutes { get; set; }

    /// <summary>Google Calendar: cada cuántos minutos se relee el calendario (1-60).</summary>
    [ObservableProperty]
    public partial int GoogleCalendarRefreshMinutes { get; set; }

    /// <summary>Google Calendar: cuántos días hacia delante se leen (1-14).</summary>
    [ObservableProperty]
    public partial int GoogleCalendarDaysAhead { get; set; }

    /// <summary>
    /// Id del cliente OAuth del usuario (tipo «Aplicación de escritorio»): es el
    /// proyecto de Google que autoriza la app, no una credencial de la app.
    /// </summary>
    [ObservableProperty]
    public partial string GoogleCalendarClientId { get; set; }

    /// <summary>
    /// Secreto del cliente OAuth. Google lo exige también en apps de escritorio y no
    /// es confidencial (viaja en el binario de cualquier app instalada), pero se
    /// guarda con los ajustes como el resto.
    /// </summary>
    [ObservableProperty]
    public partial string GoogleCalendarClientSecret { get; set; }

    /// <summary>Token de acceso vigente (se renueva solo con el de refresco).</summary>
    [ObservableProperty]
    public partial string GoogleCalendarAccessToken { get; set; }

    /// <summary>
    /// Token de refresco: es la sesión. Su presencia es lo que define «sesión
    /// iniciada»; se borra al cerrar sesión, que es lo que revoca el acceso local.
    /// </summary>
    [ObservableProperty]
    public partial string GoogleCalendarRefreshToken { get; set; }

    /// <summary>Caducidad del token de acceso, en UTC.</summary>
    [ObservableProperty]
    public partial DateTime GoogleCalendarTokenExpiresUtc { get; set; }

    /// <summary>Cuenta conectada (correo del calendario principal). Vacío = sin sesión.</summary>
    [ObservableProperty]
    public partial string GoogleCalendarAccount { get; set; }

    /// <summary>
    /// Último error del calendario en español. Vacío = sin error. Se pinta en ajustes:
    /// los fallos de red o de permiso no deben morir en el registro.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string GoogleCalendarError { get; set; }

    /// <summary>
    /// Mensaje de estado del calendario en español: «Conectando con Google…», «Sesión
    /// iniciada». No persiste (es del momento) y se pinta en ajustes.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial string GoogleCalendarStatus { get; set; }

    /// <summary>¿Hay sesión de Google iniciada? (no persiste: se deduce del token)</summary>
    [XmlIgnore]
    public bool GoogleCalendarSignedIn => GoogleCalendarRefreshToken.Length > 0;

    /// <summary>
    /// Encender o apagar los recordatorios se aplica en el acto: el contenedor reengancha
    /// sus vistas y el servicio arranca o para su bucle (con la funcionalidad apagada no
    /// se gasta red ni token leyendo el calendario).
    /// </summary>
    partial void OnIslandCalendarEnabledChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshCalendarContent();

    /// <summary>
    /// Encender o apagar los avisos de Bluetooth se aplica en el acto: el vigía
    /// arranca o se para (con la funcionalidad apagada no se observa nada) y, si su
    /// vista estaba puesta, el contenedor se repliega sin dejar una superficie vacía.
    /// </summary>
    partial void OnIslandBluetoothEnabledChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshBluetoothContent();

    /// <summary>
    /// Encender o apagar el portapapeles se aplica en el acto: arranca o se para la
    /// escucha (apagada no se observa nada) y, si su vista estaba puesta, el
    /// contenedor se repliega sin dejar una superficie vacía.
    /// </summary>
    partial void OnIslandClipboardEnabledChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshClipboardContent();

    /// <summary>
    /// El tope de la lista se aplica en el acto: el servicio recorta lo que sobre y
    /// la vista se repinta con lo que quede.
    /// </summary>
    partial void OnIslandClipboardMaxItemsChanged(int oldValue, int newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshClipboardContent();

    /// <summary>
    /// Encender o apagar el clima se aplica en el acto: arranca o se para su ciclo de
    /// refresco (apagado no se llama a la red) y, si su vista estaba puesta, el
    /// contenedor se repliega sin dejar una superficie vacía.
    /// </summary>
    partial void OnIslandWeatherEnabledChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshWeatherContent();

    /// <summary>
    /// Cambiar de lugar se aplica en el acto: el ciclo consulta el lugar nuevo en
    /// cuanto se guarda (el dato anterior se conserva hasta que llegue el suyo).
    /// </summary>
    partial void OnIslandWeatherPlaceChanged(string oldValue, string newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshWeatherContent();

    /// <summary>
    /// Encender o apagar los avisos del cargador se aplica en el acto: el vigía arranca
    /// o se para (apagado no se observa el estado de energía) y, si su vista estaba
    /// puesta, el contenedor se repliega sin dejar una superficie vacía.
    /// </summary>
    partial void OnIslandPowerEnabledChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshPowerContent();

    /// <summary>
    /// El ajuste de pantalla completa se aplica en el acto: la supresión es una
    /// instantánea cacheada, así que se invalida y se recalcula con el valor nuevo
    /// (si el Island tenía que apartarse, se aparta ya).
    /// </summary>
    partial void OnIslandHideOnFullscreenChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshSuppressionState();

    partial void OnGoogleCalendarRefreshTokenChanged(string oldValue, string newValue)
    {
        // El resto de la app se pregunta por GoogleCalendarSignedIn: al cambiar la
        // sesión hay que avisar también de esa propiedad.
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
        IslandTopOffset = 6;
        IslandVisibilityMode = 0;
        IslandReturnToInactive = true;
        IslandUltraCompact = false;
        IslandPauseCountsActive = true;
        IslandExpandTrigger = 2;
        IslandVisibilityDuration = 4000;
        IslandShowOnPlayPause = true;
        IslandShowOnPause = false;
        IslandShowOnTrackChange = true;
        IslandActivityLine = true;
        IslandEqEnabled = true;
        IslandEqCenteredBars = true;
        IslandEqBarCount = 8;
        IslandEqSensitivity = 3;
        IslandEqSmoothing = 50;
        IslandAnimated = true;
        IslandFontFamily = "Poppins";
        IslandTextStyle = 1;
        IslandCompactTitleFontSize = 12;
        IslandExpandedTitleFontSize = 12;
        IslandExpandedArtistFontSize = 12;
        IslandMediaEnabled = true;
        IslandTimerEnabled = true;
        IslandTimerShowProgress = true;
        IslandTimerShowArrows = false;
        // Vacía a propósito: el deserializador RELLENA la colección existente con
        // los presets guardados (no la reemplaza), así que sembrarla aquí los
        // duplicaba en cada arranque. Los de fábrica los aplica
        // CompleteInitialization solo cuando el archivo no trae ninguno.
        IslandTimerPresets = [];
        TimerPresetsError = "";
        IslandAppsEnabled = true;
        // Vacía a propósito, igual que los presets: el deserializador XML RELLENA
        // la colección existente, sembrarla aquí duplicaría las aplicaciones.
        IslandApps = [];
        IslandAppsError = "";
        IslandShelfEnabled = true;
        // Vacía a propósito, igual que el cajón: el deserializador XML RELLENA la
        // colección existente.
        IslandShelfItems = [];
        IslandShelfError = "";
        IslandHideOnFullscreen = true;
        IslandCalendarEnabled = false;
        // Sí por defecto: un aviso al conectar (que además usa el plazo del aviso
        // temporal) es justo lo que se espera de la funcionalidad, y no toca nada
        // del sistema: solo observa conexiones.
        IslandBluetoothEnabled = true;
        // Sí por defecto: guardar lo que se copia no cambia lo que el usuario copia
        // (el original se conserva intacto) y es justo lo que se espera de la
        // funcionalidad; apagarla desde ajustes para la escucha en el acto.
        IslandClipboardEnabled = true;
        IslandClipboardMaxItems = 25;
        // El clima necesita que el usuario elija SU lugar: nace apagado y sin lugar
        // (apagado no se consulta nada a la red).
        IslandWeatherEnabled = false;
        IslandWeatherPlace = "";
        IslandWeatherLatitude = 0;
        IslandWeatherLongitude = 0;
        IslandWeatherStatus = "";
        IslandWeatherError = "";
        // Sí por defecto: enterarse de que el cargador se soltó (o de que empezó a
        // cargar) es justo lo que se espera, y solo observa el estado que ya conoce
        // Windows.
        IslandPowerEnabled = true;
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

        // Vacía a propósito, igual que los presets, el cajón y el estante: el
        // deserializador XML RELLENA la colección existente en vez de reemplazarla,
        // así que sembrar aquí las pantallas por defecto las añadía a las del usuario
        // en CADA arranque (el usuario veía sus pantallas más las de fábrica, y el
        // guardado las escribía todas). Las de fábrica las aplica
        // CompleteInitialization solo cuando el archivo no trae ninguna.
        IslandScreens = [];
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
        // Migración del radio único heredado: los XML antiguos no traen los nuevos
        // radios (-1), así que heredan el valor que el usuario ya tenía.
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
        // Migración de presets: XML antiguos sin la colección, con presets
        // duplicados (arranques previos) o con datos inválidos.
        IslandTimerPresets ??= [];
        SanitizeTimerPresets();
        // Migración del cajón: XML antiguos sin la colección o con entradas
        // repetidas, sin nombre o por encima del máximo.
        IslandApps ??= [];
        SanitizeIslandApps();
        // Migración del estante: XML antiguos sin la colección (o con elementos cuya
        // ruta ya no existe: se sacaron con la aplicación cerrada) arrancan limpios.
        IslandShelfItems ??= [];
        SanitizeIslandShelf();
        // Migración de las pantallas: XML antiguos sin el ajuste arrancan con una
        // pantalla por funcionalidad (el comportamiento de siempre) y cualquier
        // pantalla vacía, con ids desconocidos o con más funcionalidades de las que
        // caben se sanea.
        IslandScreens ??= [.. IslandFeatureIds.DefaultScreens];
        SanitizeIslandScreens();
        // Migración del calendario: XML antiguos sin estos ajustes arrancan con la
        // funcionalidad apagada (nadie concede acceso a su calendario por sorpresa) y
        // con las ventanas por defecto. Las cadenas nunca son null tras el XML.
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
        // Autorreparación de duplicados exactos: los archivos guardados por las
        // versiones que sembraban la colección en el constructor traen la lista
        // repetida (4+4+2…). Se conserva la primera aparición de cada par
        // nombre+duración y el resto se descarta.
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
    /// Crea un preset (máx. 10, con mensaje en español al superarlo). Usado por IslandPage.
    /// </summary>
    public bool AddTimerPreset()
    {
        if (IslandTimerPresets.Count >= Classes.IslandTimer.MaxPresets)
        {
            TimerPresetsError = "Máximo 10 presets. Borra uno para crear otro.";
            return false;
        }
        IslandTimerPresets.Add(new TimerPreset { Name = $"Preset {IslandTimerPresets.Count + 1}", DurationSeconds = 300 });
        TimerPresetsError = "";
        return true;
    }

    /// <summary>
    /// Borra un preset. La cuenta en curso no cambia (spec 002, casos límite).
    /// </summary>
    public void RemoveTimerPreset(TimerPreset preset)
    {
        IslandTimerPresets.Remove(preset);
        TimerPresetsError = "";
    }

    /// <summary>
    /// Autorreparación del cajón: fuera rutas vacías o repetidas, nombre tomado
    /// del ejecutable cuando falta y recorte por encima del máximo.
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
        // El contenedor necesita saber que el cajón cambió de disponibilidad
        // (sin aplicaciones no se puede mostrar).
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppsContent();
    }

    private void IslandApp_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initializing && e.PropertyName is nameof(IslandApp.Name)) SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Añade una aplicación al cajón del Island (máx. 12 y sin repetir ruta).
    /// El nombre sale de la descripción del ejecutable. Usado por IslandPage.
    /// </summary>
    public bool AddIslandApp(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0)
        {
            IslandAppsError = "Selecciona una aplicación para añadir.";
            return false;
        }
        if (IslandApps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            IslandAppsError = "Esa aplicación ya está en el cajón.";
            return false;
        }
        if (IslandApps.Count >= IslandApp.MaxApps)
        {
            IslandAppsError = $"Máximo {IslandApp.MaxApps} aplicaciones. Quita una para añadir otra.";
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
    /// Quita una aplicación del cajón. La app ya abierta no se cierra.
    /// </summary>
    public void RemoveIslandApp(IslandApp app)
    {
        IslandApps.Remove(app);
        IslandAppsError = "";
    }

    /// <summary>
    /// Sanea las pantallas del Island: se retira la cabecera de fábrica que inyectaba el
    /// bug de arranque, se descartan las repetidas, cada pantalla se reescribe con sus
    /// funcionalidades conocidas y sin repetir, y las que se quedan sin ninguna se
    /// descartan. Una pantalla con más funcionalidades de las que caben se REPARTE en
    /// pantallas de <see cref="IslandFeatureIds.MaxFeaturesPerScreen"/> (nada se pierde
    /// al cargar un ajuste viejo o editado a mano). Sin ninguna pantalla válida rigen
    /// las de fábrica (nunca una navegación vacía).
    /// </summary>
    internal void SanitizeIslandScreens()
    {
        // Autorreparación del bug de arranque: aquella versión sembraba las pantallas de
        // fábrica en el constructor y el deserializador XML AÑADE a la colección
        // existente, así que los archivos de entonces empiezan por la lista de fábrica
        // ENTERA y EN ORDEN, delante de las pantallas del usuario (una vez por arranque).
        // Esa cabecera no la eligió nadie: se retira cuando hay algo detrás. Con la lista
        // de fábrica a solas no se toca: quien nunca editó sus pantallas conserva su
        // Island tal y como lo tenía.
        int header = LegacyFactoryScreensHeaderLength();
        if (header > 0 && header < IslandScreens.Count)
        {
            for (int at = 0; at < header; at++) IslandScreens.RemoveAt(0);
        }
        var cleaned = new List<string>();
        // Una pantalla repetida EXACTA no es una decisión del usuario: nadie quiere la
        // misma vista dos veces en el recorrido, y era justo lo que dejaba el bug.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in IslandScreens.ToList())
        {
            var ids = IslandFeatureIds.ParseScreen(screen);
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
    /// Cuántas entradas INICIALES son exactamente la lista de fábrica ANTIGUA (una
    /// pantalla por funcionalidad, en el orden de <see cref="IslandFeatureIds.All"/>), en
    /// orden: 0 si el archivo no empieza por ellas. Reconoce la cabecera que dejaba el bug
    /// de arranque, repetida tantas veces como arranques sufriera el archivo. Es una
    /// huella exacta: una pantalla por funcionalidad, todas, y en ese orden.
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
    /// Alta/baja de las pantallas: al reemplazarse la colección se reengancha el
    /// guardado y el contenedor.
    /// </summary>
    partial void OnIslandScreensChanged(ObservableCollection<string> oldValue, ObservableCollection<string> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= IslandScreens_CollectionChanged;
        if (newValue != null) newValue.CollectionChanged += IslandScreens_CollectionChanged;
    }

    private void IslandScreens_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_initializing) SettingsManager.SaveSettings();
        // El contenedor navega por estas pantallas: se releen en el acto y, si el
        // Island está a la vista, se vuelve a presentar la pantalla vigente con la
        // composición nueva, sin reiniciar la aplicación (change island-pantallas RF-2).
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshScreensContent();
    }

    // --- pantallas del Island (change island-pantallas) ---

    /// <summary>Mueve una pantalla en la lista (orden de navegación del contenedor).</summary>
    internal void MoveIslandScreen(string screen, int delta)
    {
        int from = IslandScreens.IndexOf(screen);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= IslandScreens.Count) return;
        IslandScreens.Move(from, to);
    }

    /// <summary>
    /// Añade una funcionalidad al FINAL de una pantalla. El orden de dentro de la
    /// pantalla es el orden en el que sus funcionalidades se presentan (de izquierda a
    /// derecha: las columnas del expandido, y el turno del compacto), así que la nueva
    /// entra al final y el usuario la coloca con <see cref="MoveIslandScreenFeature"/>. Una
    /// pantalla llena (<see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>) no acepta
    /// más: es el máximo que cabe en una sola pantalla del Island.
    /// </summary>
    internal bool AddIslandScreenFeature(string screen, string featureId)
    {
        if (!IslandFeatureIds.IsKnown(featureId)) return false;
        int index = IslandScreens.IndexOf(screen);
        if (index < 0) return false;
        var ids = IslandFeatureIds.ParseScreen(screen).ToList();
        if (ids.Count >= IslandFeatureIds.MaxFeaturesPerScreen) return false;
        if (ids.Contains(featureId)) return false;
        ids.Add(featureId);
        IslandScreens[index] = IslandFeatureIds.FormatScreen(ids);
        return true;
    }

    /// <summary>
    /// Mueve una funcionalidad dentro de su pantalla (el orden de presentación).
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
    /// Saca una funcionalidad de su pantalla. La última no se puede sacar: una pantalla
    /// sin funciones no tendría nada que enseñar (no existe).
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
    /// Añade una pantalla nueva. Nace con la primera funcionalidad que no esté en
    /// ninguna pantalla y, si ya están todas repartidas, con la música: una pantalla
    /// vacía no existe (no habría nada que enseñar).
    /// </summary>
    internal void AddIslandScreen()
    {
        string id = IslandFeatureIds.All.FirstOrDefault(candidate =>
            !IslandScreens.Any(screen => IslandFeatureIds.ParseScreen(screen).Contains(candidate)))
            ?? IslandFeatureIds.Media;
        IslandScreens.Add(id);
    }

    /// <summary>Quita una pantalla; la última no se puede quitar (navegación vacía).</summary>
    internal void RemoveIslandScreen(string screen)
    {
        if (IslandScreens.Count <= 1) return;
        IslandScreens.Remove(screen);
    }

    // --- estante de archivos del Island ---

    /// <summary>
    /// Carpeta propia del estante: aquí se MUEVEN los archivos y carpetas que se sueltan
    /// sobre el Island. Vive junto a los ajustes, en %APPDATA%\FluentFlyout\IslandShelf,
    /// y es la carpeta que abre el botón «Abrir carpeta» de ajustes.
    /// </summary>
    public static string IslandShelfFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "IslandShelf");

    /// <summary>
    /// Autorreparación del estante: fuera los elementos cuya ruta ya no existe (el
    /// usuario los sacó con la aplicación cerrada) y recorte por encima del máximo.
    /// Nunca toca el disco: el estante no borra archivos.
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
        // El contenedor rellena sus listas (y, con el estante a la vista, lo deja al día).
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshShelfContent();
    }

    partial void OnIslandShelfEnabledChanged(bool oldValue, bool newValue) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshShelfContent();

    // --- Google Calendar ---

    /// <summary>
    /// Cierra la sesión de Google: se borran los tokens y con ellos la capacidad de
    /// leer el calendario. Los eventos en memoria los suelta el servicio al
    /// reconfigurarse (nada de un calendario ajeno pintado tras cerrar sesión). Los
    /// ajustes del cliente OAuth se conservan para no tener que volver a pegarlos.
    /// </summary>
    public void SignOutGoogleCalendar()
    {
        GoogleCalendarAccessToken = "";
        GoogleCalendarRefreshToken = "";
        GoogleCalendarTokenExpiresUtc = DateTime.MinValue;
        GoogleCalendarAccount = "";
        GoogleCalendarError = "";
        GoogleCalendarStatus = "Sesión cerrada.";
        GoogleCalendarService.Instance.Configure();
    }

    /// <summary>
    /// Suelta archivos y carpetas en el estante: cada uno se MUEVE a la carpeta del
    /// estante —nombre libre si el suyo ya está cogido: aquí no se pisa nada— y se apunta
    /// de dónde vino, para poder devolverlo al quitarlo. Devuelve cuántos entraron.
    /// </summary>
    public int AddIslandShelfPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string? raw in paths)
        {
            string source = (raw ?? "").Trim();
            if (source.Length == 0) continue;
            // Ya está en el estante: no se duplica.
            if (IslandShelfItems.Any(i => string.Equals(i.Path, source, StringComparison.OrdinalIgnoreCase))) continue;
            if (IslandShelfItems.Count >= IslandShelfItem.MaxItems)
            {
                IslandShelfError = $"Máximo {IslandShelfItem.MaxItems} elementos en el estante.";
                break;
            }
            bool isFolder = Directory.Exists(source);
            if (!isFolder && !File.Exists(source)) continue;
            if (!TryEnsureShelfFolder()) break;
            string? parked = MoveTo(IslandShelfFolder, source, isFolder);
            if (parked == null)
            {
                IslandShelfError = "No se pudo aparcar algún elemento (revisa el registro).";
                continue;
            }
            IslandShelfItems.Add(new IslandShelfItem { Path = parked, Origin = source, IsFolder = isFolder });
            added++;
        }
        if (added > 0) IslandShelfError = "";
        return added;
    }

    /// <summary>
    /// Quita un elemento del estante devolviéndolo a su carpeta original: quitarlo no
    /// puede destruir nada del usuario. Si la carpeta original ya no existe —o el nombre
    /// está ocupado y no se pudo numerar— el elemento se queda y se explica el motivo.
    /// Devuelve true si salió del estante.
    /// </summary>
    public bool RemoveIslandShelfItem(IslandShelfItem item)
    {
        try
        {
            string originDir = System.IO.Path.GetDirectoryName(item.Origin) ?? "";
            if (originDir.Length == 0 || !Directory.Exists(originDir))
            {
                IslandShelfError = "La carpeta original ya no existe: el elemento sigue en el estante.";
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
            IslandShelfError = "No se pudo devolver el elemento a su carpeta original.";
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
            IslandShelfError = "No se pudo preparar la carpeta del estante.";
            return false;
        }
    }

    /// <summary>Mueve un archivo o carpeta a la carpeta del estante, sin pisar nada.</summary>
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
    /// Primera ruta libre de <paramref name="folder"/> para ese nombre: si ya está cogida
    /// se numera («nombre (2).ext», «nombre (3).ext»…). Nunca devuelve una ruta ocupada,
    /// así que ningún movimiento del estante pisa un archivo que ya estuviera ahí.
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

    partial void OnIslandCompactBorderRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandCompactBorderRadius = Math.Clamp(newValue, 0, 40);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandExpandedBorderRadiusChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandExpandedBorderRadius = Math.Clamp(newValue, 0, 40);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandNotchFilletCompactChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandNotchFilletCompact = Math.Clamp(newValue, 0, 20);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandNotchFilletExpandedChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandNotchFilletExpanded = Math.Clamp(newValue, 0, 20);
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
        int fixedValue = newValue == 0 ? 320 : Math.Clamp(newValue, 280, 600);
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

    partial void OnIslandVisibilityModeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandVisibilityMode = Math.Clamp(newValue, 0, 1);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshVisibilityState();
    }

    partial void OnIslandReturnToInactiveChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandUltraCompactChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshUltraCompact();
    }

    partial void OnIslandPauseCountsActiveChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshVisibilityState();
    }

    partial void OnIslandStyleChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandLineTopOffsetChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandLineTopOffset = Math.Clamp(newValue, 0, 60);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandTopOffsetChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandTopOffset = Math.Clamp(newValue, 0, 80);
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

    partial void OnIslandFontFamilyChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandTextStyleChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandCompactTitleFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandCompactTitleFontSize = Math.Clamp(newValue, 10, 24);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandExpandedTitleFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandExpandedTitleFontSize = Math.Clamp(newValue, 10, 24);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandExpandedArtistFontSizeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        IslandExpandedArtistFontSize = Math.Clamp(newValue, 10, 24);
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandMediaEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshMediaContent();
    }

    partial void OnIslandTimerEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshTimerEnabled();
    }

    partial void OnIslandAppsEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppsContent();
    }

    partial void OnIslandTimerShowProgressChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
    }

    partial void OnIslandTimerShowArrowsChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshAppearance();
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
        // El toggle del Media Flyout solo afecta a la ventana emergente de
        // música; el Island sigue las sesiones del sistema por sí mismo.
    }

    partial void OnLockKeysEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;
        mainWindow.RefreshKeyboardHook();
    }
}
