// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace FluentFlyout.Controls;

/// <summary>
/// Interaction logic for TaskbarWidgetControl.xaml
/// </summary>
public partial class TaskbarWidgetControl : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly double _scale = 0.9;
    private readonly int _nativeWidgetsPadding = 216;

    private readonly int _coverImageMargin = 55;

    // Cached width calculations
    private string _cachedTitleText = string.Empty;
    private string _cachedArtistText = string.Empty;
    private double _cachedTitleWidth = 0;
    private double _cachedArtistWidth = 0;
    private double _cachedTitleContainerWidth = -1;
    private double _cachedArtistContainerWidth = -1;
    private readonly int _extraMarginForText = 6; // additional margin to avoid text clipping

    private double _cachedTitleOpacityMaskWidth = -1;
    private double _cachedArtistOpacityMaskWidth = -1;
    private LinearGradientBrush? _cachedTitleOpacityMask;
    private LinearGradientBrush? _cachedArtistOpacityMask;

    private string _actualTitle = string.Empty;
    private string _actualArtist = string.Empty;

    // reference to main window for flyout functions
    private MainWindow? _mainWindow;
    private bool _isPaused;

    // rotating background (baked blur)
    private double _appliedRotationDurationSeconds;
    private BitmapImage? _currentIcon;
    private BitmapImage? _bakedIcon;
    private BitmapSource? _bakedBackground;
    private double _bakedSideDip;
    private RotateTransform? _backgroundRotateTransform;
    private bool _backgroundRotationActive;
    private bool _backgroundRotationAnimationRunning;
    private bool _backgroundRotationWasUp;
    private bool _backgroundRotationPaused;
    private double _pausedRotationAngle;

    public TaskbarWidgetControl()
    {
        InitializeComponent();

        // Apply Windows theme colors (independent of the app theme setting)
        ApplyWindowsTheme();

        // Set DataContext for bindings
        DataContext = SettingsManager.Current;

        MainBorder.SizeChanged += (s, e) =>
        {
            ApplyCornerRadius();

            if (_backgroundRotationActive)
                ApplyBackgroundRotation();
            else
                LayoutBackgroundToFillWidget();
        };
        ApplyCornerRadius();

        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // Initialize control order
        ReorderControls();

        // Apply the background mode (normal or animated rotation)
        UpdateBackgroundMode();
    }

    public void ApplyCornerRadius()
    {
        double radius = SettingsManager.Current.TaskbarWidgetBorderRadius;
        MainBorder.CornerRadius = new CornerRadius(radius);
        TopBorder.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
        SongImageBorder.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
        MainBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, MainBorder.ActualWidth, MainBorder.ActualHeight), radius, radius);
    }

    public void ReorderControls()
    {
        // Remove ControlsStackPanel from MainStackPanel
        MainStackPanel.Children.Remove(ControlsStackPanel);

        // Reorder based on position setting
        if (SettingsManager.Current.TaskbarWidgetControlsPosition == 0)
        {
            // Left: Controls, Image, Info
            MainStackPanel.Children.Insert(0, ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(2, 0, 6, 0); // for some reason margins are weird on left side
        }
        else
        {
            // Right: Image, Info, Controls
            MainStackPanel.Children.Add(ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    public void SetVerticalMode(bool isVertical)
    {
        var counterRotate = isVertical ? new RotateTransform(-90) : null;

        SongImageBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        SongImageBorder.RenderTransform = (Transform?)counterRotate ?? Transform.Identity;

        foreach (var button in new Wpf.Ui.Controls.Button[] { PreviousButton, PlayPauseButton, NextButton })
        {
            button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            button.RenderTransform = (Transform?)counterRotate ?? Transform.Identity;
        }
    }

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void ApplyWindowsTheme()
    {
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;

        var foreground = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE4, 0x1C, 0x1C, 0x1C));

        SongTitle.Foreground = foreground;
        SongArtist.Foreground = foreground;
        PreviousButton.Foreground = foreground;
        PlayPauseButton.Foreground = foreground;
        NextButton.Foreground = foreground;
    }

    /// <summary>
    /// Applies the background mode based on settings: either the normal live-blurred image
    /// or the complete square album disc rotating behind the widget (the widget only acts
    /// as the viewport that reveals a band of the rotating square).
    /// </summary>
    public void UpdateBackgroundMode()
    {
        bool shouldRotate = SettingsManager.Current.TaskbarWidgetBackgroundRotate &&
                            SettingsManager.Current.TaskbarWidgetBackgroundBlur;

        if (shouldRotate)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            StopBackgroundRotation();

            LayoutBackgroundToFillWidget();

            if (_currentIcon != null)
                BackgroundImage.Source = _currentIcon;
        }

        UpdateRotationPauseState();
    }

    private void ApplyBackgroundRotation(double startAngle = 0)
    {
        double width = MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240;
        double height = MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40;

        _backgroundRotationActive = true;

        if (_backgroundRotateTransform == null)
        {
            _backgroundRotateTransform = new RotateTransform(0);
            BackgroundImage.RenderTransform = _backgroundRotateTransform;
        }

        // blur is baked into the bitmap, so disable the live effect while rotating
        BackgroundImage.Effect = null;

        // The rotating square is about three times the widget's width - large enough that a
        // full viewport-width band always stays inside the rotating disc at every angle,
        // while still keeping the artwork recognisable. It is sized in DIPs from the
        // widget itself, so it scales with any screen/DPI (1080p, 4K, ...).
        double sizeMultiplier = Math.Max(SettingsManager.Current.TaskbarWidgetBackgroundRotateSize, 100) / 100.0;
        double discSide = Math.Max(Math.Max(width * sizeMultiplier, height * sizeMultiplier), 720);
        double offsetX = discSide * 0.28; // distance of the viewport band from the disc centre

        BackgroundImage.Width = discSide;
        BackgroundImage.Height = discSide;
        BackgroundImage.Stretch = Stretch.Fill;
        BackgroundImage.Margin = new Thickness(0);

        // The square is centred vertically, but shifted horizontally away from the disc
        // centre so the centre of the album is never visible through the viewport.
        bool showLeftSide = SettingsManager.Current.TaskbarWidgetBackgroundRotateSide == 0;
        Canvas.SetLeft(BackgroundImage, (width - discSide) / 2 + (showLeftSide ? offsetX : -offsetX));
        Canvas.SetTop(BackgroundImage, (height - discSide) / 2);

        if (_currentIcon != null)
            BackgroundImage.Source = GetBakedBackground(_currentIcon, discSide);

        // 0 = spins down (clockwise), 1 = spins up (counter - clockwise)
        bool spinUp = SettingsManager.Current.TaskbarWidgetBackgroundRotateDirection == 1;

        // Restart the endless rotation when a different direction, duration or start angle
        // (e.g. resuming after a pause) is requested
        double durationSeconds = Math.Max(SettingsManager.Current.TaskbarWidgetBackgroundRotateDuration, 1);
        if (_backgroundRotationPaused)
        {
            // keep the disc frozen at the angle it was paused at, even if the layout
            // changed (e.g. widget resize or settings change) while media is paused
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            _backgroundRotateTransform.Angle = _pausedRotationAngle;
            return;
        }

        if (!_backgroundRotationAnimationRunning ||
            _backgroundRotationWasUp != spinUp ||
            Math.Abs(_appliedRotationDurationSeconds - durationSeconds) > 0.01 ||
            Math.Abs(startAngle - 0) > 0.01)
        {
            _backgroundRotationWasUp = spinUp;
            _backgroundRotationAnimationRunning = true;
            _appliedRotationDurationSeconds = durationSeconds;
            var animation = new DoubleAnimation
            {
                From = startAngle,
                To = spinUp ? startAngle - 360 : startAngle + 360,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                RepeatBehavior = RepeatBehavior.Forever
            };
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
    }

    /// <summary>
    /// Freezes the rotating background at its current angle. The animation clock is
    /// stopped but the disc stays visible at the exact angle it had when paused.
    /// </summary>
    private void PauseBackgroundRotation()
    {
        if (!_backgroundRotationAnimationRunning || _backgroundRotationPaused || _backgroundRotateTransform == null)
            return;

        _pausedRotationAngle = _backgroundRotateTransform.Angle;
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _backgroundRotateTransform.Angle = _pausedRotationAngle;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = true;
    }

    /// <summary>
    /// Resumes the rotating background animation from the angle it was paused at.
    /// </summary>
    private void ResumeBackgroundRotation()
    {
        if (!_backgroundRotationPaused)
            return;

        _backgroundRotationPaused = false;
        ApplyBackgroundRotation(_pausedRotationAngle);
    }

    /// <summary>
    /// Pauses or resumes the rotating background to match the current media playback
    /// state so the spinning disc freezes while media is paused.
    /// </summary>
    private void UpdateRotationPauseState()
    {
        if (!SettingsManager.Current.TaskbarWidgetBackgroundRotate ||
            !SettingsManager.Current.TaskbarWidgetBackgroundBlur)
            return;

        if (_isPaused)
            PauseBackgroundRotation();
        else
            ResumeBackgroundRotation();
    }

    private void StopBackgroundRotation()
    {
        _backgroundRotationActive = false;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = false;
        if (_backgroundRotateTransform != null)
        {
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            _backgroundRotateTransform.Angle = 0;
        }
        BackgroundImage.Effect = BackgroundImageBlurEffect;
    }

    /// <summary>
    /// Lays out the background for the normal (non-rotating) blurred mode. A square
    /// element the size of the widget's larger dimension is centred over the viewport,
    /// so the visible band is cropped from the middle of the artwork, not its top.
    /// </summary>
    private void LayoutBackgroundToFillWidget()
    {
        double width = BackgroundCanvas.ActualWidth > 0 ? BackgroundCanvas.ActualWidth : (MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240);
        double height = BackgroundCanvas.ActualHeight > 0 ? BackgroundCanvas.ActualHeight : (MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40);

        double side = Math.Max(Math.Max(width, height), 1);

        Canvas.SetLeft(BackgroundImage, (width - side) / 2);
        Canvas.SetTop(BackgroundImage, (height - side) / 2);
        BackgroundImage.Width = side;
        BackgroundImage.Height = side;
        BackgroundImage.Margin = new Thickness(0);
        BackgroundImage.Stretch = Stretch.UniformToFill;
    }

    private void SetBackground(BitmapImage? icon)
    {
        _currentIcon = icon;

        if (icon == null)
        {
            StopBackgroundRotation();
            Canvas.SetLeft(BackgroundImage, 0);
            Canvas.SetTop(BackgroundImage, 0);
            BackgroundImage.Source = null;
            return;
        }

        if (SettingsManager.Current.TaskbarWidgetBackgroundRotate &&
            SettingsManager.Current.TaskbarWidgetBackgroundBlur)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            LayoutBackgroundToFillWidget();
            BackgroundImage.Source = icon;
        }
    }

    /// <summary>
    /// Bakes the complete album cover once into a large blurred square that covers the
    /// widget while rotating, avoiding per-frame software re-rasterization of a live effect.
    /// </summary>
    private BitmapSource? GetBakedBackground(BitmapImage icon, double discSide)
    {
        if (_bakedBackground != null && ReferenceEquals(_bakedIcon, icon) && Math.Abs(_bakedSideDip - discSide) < 0.5)
            return _bakedBackground;

        try
        {
            // Bake the texture at a resolution that matches the on-screen size of the
            // square (discSide DIPs * monitor DPI), clamped to keep memory reasonable.
            // This keeps the rotating background sharp on any screen size (1080p, 4K, ...).
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int res = (int)Math.Ceiling(discSide * dpi);
            res = Math.Max(res, 512);
            res = Math.Min(res, 4096);
            int pixelSide = res;

            // scale the user's blur radius (DIPs on screen) into the baked texture space
            double blurRadius = SettingsManager.Current.TaskbarWidgetBackgroundBlurRadius * res / Math.Max(discSide, 1);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // the complete, unedited album cover, stretched to fill the square disc
                dc.DrawImage(icon, new Rect(0, 0, res, res));
            }

            visual.Effect = new BlurEffect
            {
                Radius = blurRadius,
                KernelType = KernelType.Gaussian
            };

            var rtb = new RenderTargetBitmap(pixelSide, pixelSide, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            _bakedIcon = icon;
            _bakedBackground = rtb;
            _bakedSideDip = discSide;
            return _bakedBackground;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to bake blurred taskbar widget background");
            _bakedIcon = null;
            _bakedBackground = null;
            _bakedSideDip = 0;
            return null;
        }
    }

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mainWindow == null) return;

        // toggle main flyout when clicked
        if (SettingsManager.Current.TaskbarWidgetClickOpensFlyout)
            _mainWindow.ShowMediaFlyout(toggleMode: true, forceShow: true);
    }

    public (double logicalWidth, double logicalHeight) CalculateSize(double dpiScale)
    {
        // calculate widget width - use cached values if text hasn't changed
        string currentTitle = _actualTitle;
        string currentArtist = _actualArtist;

        bool textChanged = false;

        if (!string.Equals(currentTitle, _cachedTitleText, StringComparison.Ordinal))
        {
            _cachedTitleWidth = Math.Round(StringWidth.GetStringWidth(currentTitle, 400), 2);
            _cachedTitleText = currentTitle;
            textChanged = true;
        }
        if (!string.Equals(currentArtist, _cachedArtistText, StringComparison.Ordinal))
        {
            _cachedArtistWidth = Math.Round(StringWidth.GetStringWidth(currentArtist, 400), 2);
            _cachedArtistText = currentArtist;
            textChanged = true;
        }

        // maximum width limit, same as Windows native widget
        double maxLogicalWidth = _nativeWidgetsPadding / _scale;
        double logicalWidth;

        double coverImageReserved = SettingsManager.Current.TaskbarWidgetShowAlbumArt ? _coverImageMargin : 0;

        if (SettingsManager.Current.TaskbarWidgetFixedWidth)
        {
            // pin to the user-configured width so right-aligned controls don't shift between songs
            logicalWidth = SettingsManager.Current.TaskbarWidgetFixedWidthPx / _scale;
            logicalWidth = Math.Min(logicalWidth, maxLogicalWidth);
        }
        else
        {
            logicalWidth = Math.Max(_cachedTitleWidth, _cachedArtistWidth) + coverImageReserved + _extraMarginForText; // add margin for cover image
            logicalWidth = Math.Min(logicalWidth, maxLogicalWidth);
        }

        double newTitleContainerWidth = Math.Max(logicalWidth - coverImageReserved, 0);
        double newArtistContainerWidth = Math.Max(logicalWidth - coverImageReserved, 0);
        bool widthChanged = false;

        if (_cachedTitleContainerWidth != newTitleContainerWidth)
        {
            SongTitleContainer.Width = newTitleContainerWidth;
            _cachedTitleContainerWidth = newTitleContainerWidth;
            widthChanged = true;
        }

        if (_cachedArtistContainerWidth != newArtistContainerWidth)
        {
            SongArtistContainer.Width = newArtistContainerWidth;
            _cachedArtistContainerWidth = newArtistContainerWidth;
            widthChanged = true;
        }

        // Refresh animations if layout bounds or text contents change
        if (textChanged || widthChanged)
        {
            UpdateMarquees();
        }

        // add space for playback controls if enabled and visible
        if (SettingsManager.Current.TaskbarWidgetControlsEnabled && ControlsStackPanel.Visibility == Visibility.Visible)
        {
            logicalWidth += 104;
        }

        double logicalHeight = 40; // default height

        return (logicalWidth, logicalHeight);
    }

    public void UpdateMarquees()
    {
        double titleAvailableWidth = double.IsNaN(SongTitleContainer.Width) ? 0 : SongTitleContainer.Width;
        double artistAvailableWidth = double.IsNaN(SongArtistContainer.Width) ? 0 : SongArtistContainer.Width;

        bool isScrollingEnabled = SettingsManager.Current.TaskbarWidgetScrollingEnabled;

        UpdateMarquee(SongTitle, SongTitleContainer, _cachedTitleWidth, titleAvailableWidth, isScrollingEnabled);
        UpdateMarquee(SongArtist, SongArtistContainer, _cachedArtistWidth, artistAvailableWidth, isScrollingEnabled);
    }

    private void UpdateMarquee(System.Windows.Controls.TextBlock textBlock, Canvas container, double textWidth, double availableWidth, bool isEnabled)
    {
        if (textBlock.RenderTransform as TranslateTransform is not { } transform) return;

        int speed = SettingsManager.Current.TaskbarWidgetScrollingTextSpeed;
        bool loopForever = SettingsManager.Current.TaskbarWidgetScrollingTextLoopForever;
        bool isTitle = textBlock == SongTitle;
        double containerWidth = container.Width;

        // references moved outside so they may be called in the else block later
        ref double cachedMaskWidth = ref (isTitle ? ref _cachedTitleOpacityMaskWidth : ref _cachedArtistOpacityMaskWidth);
        ref LinearGradientBrush? cachedMask = ref (isTitle ? ref _cachedTitleOpacityMask : ref _cachedArtistOpacityMask);

        if (isEnabled && textWidth > availableWidth && containerWidth > 0 && !double.IsNaN(containerWidth))
        {
            textBlock.Width = double.NaN;
            textBlock.TextTrimming = TextTrimming.None;

            string origText = isTitle ? _actualTitle : _actualArtist;

            if (cachedMask == null || Math.Abs(containerWidth - cachedMaskWidth) > 0.5)
            {
                // 12.0 is the width in pixels of the gradient fade on the left and right hand edges of the 
                // text container.
                double fadeFraction = 12.0 / containerWidth;
                if (fadeFraction > 0.5) fadeFraction = 0.5;

                cachedMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(containerWidth, 0),
                    MappingMode = BrushMappingMode.Absolute
                };

                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), fadeFraction));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 1.0 - fadeFraction));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
                cachedMaskWidth = containerWidth;
            }

            container.OpacityMask = cachedMask;

            if (loopForever)
            {
                // continuous looping should have the fades constantly active (as its infinite)
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[0].Color = Color.FromArgb(0, 255, 255, 255);
                cachedMask.GradientStops[3].Color = Color.FromArgb(0, 255, 255, 255);

                // \u00A0 are non-breaking spaces, which prevents WPF from collapsing and/or trimming
                // them
                string spacer = "\u00A0\u00A0\u00A0\u00A0\u00A0";
                textBlock.Text = origText + spacer + origText;

                double spacerWidth = StringWidth.GetStringWidth(spacer, 400);
                double scrollDistance = textWidth + spacerWidth;

                double durationToScroll = scrollDistance / speed;
                var animation = new DoubleAnimation
                {
                    From = 0,
                    To = -scrollDistance,
                    Duration = TimeSpan.FromSeconds(durationToScroll),
                    RepeatBehavior = RepeatBehavior.Forever
                };

                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            else
            {
                // Adding 10 pixels gives extra padding so the text scrolls past the container's edge before
                // resetting or reversing; this prevents abrupt cutoffs
                double scrollDistance = textWidth - containerWidth + 10;
                textBlock.Text = origText;

                double durationSeconds = scrollDistance / speed;
                double pauseDuration = 2.0; // wait 2 seconds at the start and end of the scroll
                double tWaitStart = pauseDuration;
                double tScrollEnd = tWaitStart + durationSeconds;
                double tWaitEnd = tScrollEnd + pauseDuration;
                double tScrollBackEnd = tWaitEnd + durationSeconds;
                double tTotalCycle = tScrollBackEnd + pauseDuration;

                var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollBackEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                // sync fades with the "ping pong" movement
                Color transparentWhite = Color.FromArgb(0, 255, 255, 255);
                Color solidWhite = Color.FromArgb(255, 255, 255, 255);

                // 300 ms is the capped duration for the fade transition; we clamp it so that the fade animation
                // doesn't overlap with the scroll animation on certain shorter texts
                TimeSpan fadeTime = TimeSpan.FromMilliseconds(Math.Min(300, durationSeconds * 1000 / 2.0));

                var leftColorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                leftColorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart))));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart) + fadeTime)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) - fadeTime)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                var rightColorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                rightColorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd) - fadeTime)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) + fadeTime)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, leftColorAnim);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, rightColorAnim);

                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
        }
        else
        {
            if (cachedMask != null)
            {
                // Prevent memory leaks and/or unwanted behavior by clearing the color animations when the mask is hidden
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            textBlock.Text = isTitle ? _actualTitle : _actualArtist;
            textBlock.Width = containerWidth;
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            container.OpacityMask = null;
        }
    }

    public void UpdateUi(string title, string artist, BitmapImage? icon, GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus, GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls = null)
    {
        if (title == "-" && artist == "-")
        {
            // No media playing, hide UI
            Dispatcher.Invoke(() =>
            {
                _actualTitle = string.Empty;
                _actualArtist = string.Empty;

                if (SettingsManager.Current.TaskbarWidgetHideCompletely)
                {
                    Visibility = Visibility.Collapsed;
                    return;
                }

                ControlsStackPanel.Visibility = Visibility.Collapsed;
                SongTitle.Text = string.Empty;
                SongArtist.Text = string.Empty;
                SongInfoStackPanel.Visibility = Visibility.Collapsed;
                SongInfoStackPanel.ToolTip = string.Empty;
                SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
                SongImagePlaceholder.Visibility = Visibility.Visible;
                SongImage.ImageSource = null;
                SetBackground(null);
                SongImageBorder.Margin = new Thickness(0, 0, 0, -3); // align music note better when no cover

                MainBorder.Background = new SolidColorBrush(Colors.Transparent);
                MainBorder.Background.Opacity = 0;
                TopBorder.BorderBrush = Brushes.Transparent;

                Visibility = Visibility.Visible;
            });
            return;
        }

        _isPaused = false;
        if (playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            _isPaused = true;
        }

        // adjust UI based on available controls
        Dispatcher.Invoke(() =>
        {
            if (SettingsManager.Current.TaskbarWidgetControlsEnabled && playbackControls != null)
            {
                PreviousButton.IsHitTestVisible = playbackControls.IsPreviousEnabled;
                PlayPauseButton.IsHitTestVisible = playbackControls.IsPauseEnabled || playbackControls.IsPlayEnabled;
                NextButton.IsHitTestVisible = playbackControls.IsNextEnabled;

                PreviousButton.Opacity = playbackControls.IsPreviousEnabled ? 1 : 0.5;
                PlayPauseButton.Opacity = (playbackControls.IsPauseEnabled || playbackControls.IsPlayEnabled) ? 1 : 0.5;
                NextButton.Opacity = playbackControls.IsNextEnabled ? 1 : 0.5;
            }
            else
            {
                PreviousButton.IsHitTestVisible = false;
                PlayPauseButton.IsHitTestVisible = false;
                NextButton.IsHitTestVisible = false;

                PreviousButton.Opacity = 0.5;
                NextButton.Opacity = 0.5;
                PlayPauseButton.Opacity = 0.5;
            }
        });

        Dispatcher.Invoke(() =>
        {
            string newTitle = !string.IsNullOrEmpty(title) ? title : "-";
            string newArtist = !string.IsNullOrEmpty(artist) ? artist : "-";

            if (_actualTitle != newTitle || _actualArtist != newArtist)
            {
                // changed info
                if (SettingsManager.Current.TaskbarWidgetAnimated)
                {
                    AnimateEntrance();
                }

                _actualTitle = newTitle;
                _actualArtist = newArtist;

                SongTitle.Text = _actualTitle;
                SongArtist.Text = _actualArtist;
            }

            // Update tooltip with song info
            SongInfoStackPanel.ToolTip = string.Empty;
            SongInfoStackPanel.ToolTip += !string.IsNullOrEmpty(title) ? title : string.Empty;
            SongInfoStackPanel.ToolTip += !string.IsNullOrEmpty(artist) ? "\n\n" + artist : string.Empty;

            if (SettingsManager.Current.TaskbarWidgetControlsEnabled)
            {
                PlayPauseButton.Icon = _isPaused ? new SymbolIcon(SymbolRegular.Play24, filled: true) : new SymbolIcon(SymbolRegular.Pause24, filled: true);
            }

            // change color of icon
            SolidColorBrush brush = BitmapHelper.SavedDominantColors.Count > 0 ?
                BitmapHelper.SavedDominantColors.Last()
                : (SolidColorBrush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorTertiary");
            SongImagePlaceholder.Foreground = brush;

            if (icon != null)
            {
                if (_isPaused && SettingsManager.Current.TaskbarWidgetShowPauseOverlay)
                { // show pause icon overlay
                    SongImagePlaceholder.Symbol = SymbolRegular.Pause24;
                    SongImagePlaceholder.Visibility = Visibility.Visible;
                    SongImage.Opacity = 0.4;
                }
                else
                {
                    SongImagePlaceholder.Visibility = Visibility.Collapsed;
                    SongImage.Opacity = 1;
                }
                SongImage.ImageSource = icon;
                SetBackground(icon);
                SongImageBorder.Margin = new Thickness(0, 0, 0, -2); // align image better when cover is present
            }
            else
            {
                SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
                SongImagePlaceholder.Visibility = Visibility.Visible;
                SongImage.ImageSource = null;
                SetBackground(null);
            }

            SongTitle.Visibility = Visibility.Visible;
            SongArtist.Visibility = !string.IsNullOrEmpty(artist) ? Visibility.Visible : Visibility.Collapsed; // hide artist if it's not available
            SongInfoStackPanel.Visibility = Visibility.Visible;
            BackgroundImage.Visibility = SettingsManager.Current.TaskbarWidgetBackgroundBlur ? Visibility.Visible : Visibility.Collapsed;

            // on top of XAML visibility binding (XAML binding only hides when disabled in settings)
            ControlsStackPanel.Visibility = SettingsManager.Current.TaskbarWidgetControlsEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateRotationPauseState();

            Visibility = Visibility.Visible;
        });
    }

    private async void AnimateEntrance()
    {
        try
        {
            int msDuration = MainWindow.getDuration();

            // opacity and left to right animation for SongInfoStackPanel
            DoubleAnimation opacityAnimation = new()
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            DoubleAnimation translateAnimation = new()
            {
                From = -10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            // Apply animations
            SongInfoStackPanel.BeginAnimation(OpacityProperty, opacityAnimation);
            TranslateTransform translateTransform = new();
            SongInfoStackPanel.RenderTransform = translateTransform;
            translateTransform.BeginAnimation(TranslateTransform.XProperty, translateAnimation);

            // don't play ControlsStackPanel animation if it's not enabled
            if (!SettingsManager.Current.TaskbarWidgetControlsEnabled)
                return;

            ControlsStackPanel.BeginAnimation(OpacityProperty, opacityAnimation);
            TranslateTransform translateTransform2 = new();
            ControlsStackPanel.RenderTransform = translateTransform2;
            translateTransform2.BeginAnimation(TranslateTransform.XProperty, translateAnimation);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during entrance animation");
        }
    }

    // event handlers for media control buttons
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var focusedSession = _mainWindow.GetActiveMediaSession();
        if (focusedSession == null) return;

        await focusedSession.ControlSession.TrySkipPreviousAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var focusedSession = _mainWindow.GetActiveMediaSession();
        if (focusedSession == null) return;

        await focusedSession.ControlSession.TryTogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var focusedSession = _mainWindow.GetActiveMediaSession();
        if (focusedSession == null) return;

        await focusedSession.ControlSession.TrySkipNextAsync();
    }
}