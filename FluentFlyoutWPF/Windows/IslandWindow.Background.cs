// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Fondo del Island: la carátula como viewport desenfocado (y opcionalmente
/// giratorio), con crossfade real a dos capas en cada cambio de álbum.
/// Parte del IslandWindow; el estado vive en <c>IslandWindow.xaml.cs</c>.
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Re-evalúa el modo de fondo (desactivado, fijo o giratorio) tras cambiar
    /// una carátula o un ajuste. Sin carátula o con el fondo apagado, las capas
    /// se retiran del árbol: nada de fondos vacíos ni residuos (001 RF-19).
    /// </summary>
    public void UpdateBackgroundMode()
    {
        ApplyBackgroundSettings();

        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandBackgroundBlur;
        if (!enabled || _backgroundIcon == null)
        {
            StopBackgroundRotation();
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImageNext.Visibility = Visibility.Collapsed;
            return;
        }

        BackgroundImage.Visibility = Visibility.Visible;
        if (SettingsManager.Current.IslandBackgroundRotate)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            StopBackgroundRotation();
            LayoutBackgroundToFill();
            BeginBackgroundCrossfade(_backgroundIcon, Math.Max(MainWindow.getDuration(), 1));
        }

        UpdateRotationPauseState();
    }

    /// <summary>Ajuste de frecuencia alta/30 FPS del giro: reaplica el reloj sin saltar de ángulo.</summary>
    public void RefreshBackgroundRotationFrameRate()
    {
        if (!SettingsManager.Current.IslandBackgroundBlur ||
            !SettingsManager.Current.IslandBackgroundRotate ||
            !_backgroundRotationActive ||
            _backgroundRotateTransform == null)
            return;

        ApplyBackgroundRotation(_backgroundRotateTransform.Angle);
    }

    private void ApplyBackgroundSettings()
    {
        double opacity = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        double radius = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurRadius, 0, 150);
        // ponytail: no tocar opacidades a mitad de fade, el snap se vería como parpadeo
        if (_backgroundCrossfadeTarget != null)
        {
            BackgroundImageBlurEffect.Radius = radius;
            BackgroundImageNextBlurEffect.Radius = radius;
            return;
        }
        BackgroundImage.Opacity = opacity;
        BackgroundImageNext.Opacity = 0;
        BackgroundImageBlurEffect.Radius = radius;
        BackgroundImageNextBlurEffect.Radius = radius;
    }

    private void ApplyBackgroundRotation(double? forcedStartAngle = null)
    {
        double width = IslandBox.Width > 0 ? IslandBox.Width : 240;
        double height = IslandBox.Height > 0 ? IslandBox.Height : 34;
        _backgroundRotationActive = true;

        _backgroundRotateTransform ??= new RotateTransform();
        BackgroundImage.RenderTransform = _backgroundRotateTransform;
        BackgroundImageNext.RenderTransform = _backgroundRotateTransform;
        BackgroundImage.Effect = null;
        BackgroundImageNext.Effect = null;
        BackgroundImage.CacheMode ??= new BitmapCache(0.5);
        BackgroundImageNext.CacheMode ??= new BitmapCache(0.5);

        double sizeMultiplier = Math.Max(SettingsManager.Current.IslandBackgroundRotateSize, 100) / 100.0;
        double discSide = Math.Max(Math.Max(ContentExpandedWidth * sizeMultiplier, height * sizeMultiplier), ContentExpandedWidth);
        double offsetX = discSide * 0.28;
        bool showLeftSide = SettingsManager.Current.IslandBackgroundRotateSide == 0;
        LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
        LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);

        if (_backgroundIcon != null)
            UpdateBakedBackgroundAsync(_backgroundIcon, discSide);

        if (_backgroundRotationPaused)
            return;

        double durationSeconds = Math.Max(SettingsManager.Current.IslandBackgroundRotateDuration, 1);
        bool spinUp = SettingsManager.Current.IslandBackgroundRotateDirection == 1;
        int? desiredFrameRate = SettingsManager.Current.IslandBackgroundRotateHighRefreshRate ? null : 30;
        bool restart = forcedStartAngle.HasValue ||
                       !_backgroundRotationAnimationRunning ||
                       _backgroundRotationWasUp != spinUp ||
                       Math.Abs(_appliedRotationDurationSeconds - durationSeconds) > 0.01 ||
                       _appliedDesiredFrameRate != desiredFrameRate;
        if (!restart) return;

        double startAngle = forcedStartAngle ?? _backgroundRotateTransform.Angle;
        _backgroundRotationWasUp = spinUp;
        _backgroundRotationAnimationRunning = true;
        _appliedRotationDurationSeconds = durationSeconds;
        _appliedDesiredFrameRate = desiredFrameRate;
        var animation = new DoubleAnimation
        {
            From = startAngle,
            To = spinUp ? startAngle - 360 : startAngle + 360,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(animation, desiredFrameRate);
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void LayoutBackground(double width, double height)
    {
        BackgroundCanvas.Width = width;
        BackgroundCanvas.Height = height;
        if (_backgroundRotationActive)
        {
            double sizeMultiplier = Math.Max(SettingsManager.Current.IslandBackgroundRotateSize, 100) / 100.0;
            double discSide = Math.Max(Math.Max(ContentExpandedWidth * sizeMultiplier, height * sizeMultiplier), ContentExpandedWidth);
            double offsetX = discSide * 0.28;
            bool showLeftSide = SettingsManager.Current.IslandBackgroundRotateSide == 0;
            LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
            LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);
        }
        else
        {
            LayoutBackgroundToFill();
        }
    }

    private void LayoutBackgroundToFill()
    {
        double width = IslandBox.Width > 0 ? IslandBox.Width : 240;
        double height = IslandBox.Height > 0 ? IslandBox.Height : 34;
        double side = Math.Max(Math.Max(width, height), 1);
        BackgroundCanvas.Width = width;
        BackgroundCanvas.Height = height;
        LayoutFillLayer(BackgroundImage, width, height, side);
        LayoutFillLayer(BackgroundImageNext, width, height, side);
    }

    private static void LayoutFillLayer(Image layer, double width, double height, double side)
    {
        layer.Width = side;
        layer.Height = side;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.UniformToFill;
        Canvas.SetLeft(layer, (width - side) / 2);
        Canvas.SetTop(layer, (height - side) / 2);
    }

    private static void LayoutDiscLayer(Image layer, double width, double height, double discSide, double offsetX, bool showLeftSide)
    {
        layer.Width = discSide;
        layer.Height = discSide;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.Fill;
        Canvas.SetLeft(layer, (width - discSide) / 2 + (showLeftSide ? offsetX : -offsetX));
        Canvas.SetTop(layer, (height - discSide) / 2);
    }

    /// <summary>
    /// Portada de fondo (o null para retirarla). Un cambio de imagen invalida el
    /// horneado en vuelo; con null se apaga el giro y se vacían las capas.
    /// </summary>
    private void SetBackground(BitmapImage? icon)
    {
        if (!ReferenceEquals(_backgroundIcon, icon))
        {
            _backgroundGeneration++;
            _bakingIcon = null;
        }
        _backgroundIcon = icon;
        if (icon == null)
        {
            StopBackgroundRotation();
            BackgroundImage.Source = null;
            ParkBackgroundNextLayer();
            BackgroundImage.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBackgroundMode();
    }

    /// <summary>
    /// Pre-desenfoca la carátula a un bitmap de 256 px (WinUI/WPF sin BlurEffect
    /// por frame): el coste se paga una vez por canción y no por fotograma.
    /// </summary>
    private static BitmapSource? BakeBlurredBackground(BitmapImage icon, double discSide, double blurRadiusDips)
    {
        const int resolution = 256;
        double blurRadius = blurRadiusDips * resolution / Math.Max(discSide, 1);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawImage(icon, new Rect(0, 0, resolution, resolution));

        visual.Effect = new BlurEffect
        {
            Radius = blurRadius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };

        var bitmap = new RenderTargetBitmap(resolution, resolution, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Horneado asíncrono con guard de versión + tamaño cuantizado (16 DIPs):
    /// ráfagas de cambios de canción no lanzan dos horneados ni republican uno viejo.
    /// </summary>
    private async void UpdateBakedBackgroundAsync(BitmapImage icon, double discSide)
    {
        int version = _backgroundGeneration;
        double bakeSide = Math.Round(discSide / 16.0) * 16.0;
        int blurRadius = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurRadius, 0, 150);
        if (_bakedBackground != null && ReferenceEquals(_bakedIcon, icon) &&
            Math.Abs(_bakedSideDip - bakeSide) < 0.5 && _bakedBlurRadius == blurRadius)
        {
            if (_backgroundRotationActive && !ReferenceEquals(BackgroundImage.Source, _bakedBackground))
                BeginBackgroundCrossfade(_bakedBackground, Math.Max(MainWindow.getDuration(), 1));
            return;
        }

        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5 && _bakingBlurRadius == blurRadius)
            return;

        if (_bakedBackground == null)
        {
            BackgroundImage.Source = icon;
            BackgroundImage.Effect = BackgroundImageBlurEffect;
        }

        _bakingIcon = icon;
        _bakingSide = bakeSide;
        _bakingBlurRadius = blurRadius;
        BitmapSource? baked;
        try
        {
            baked = await Task.Run(() => BakeBlurredBackground(icon, bakeSide, blurRadius));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to bake Fluent Island background");
            if (ReferenceEquals(_bakingIcon, icon)) _bakingIcon = null;
            return;
        }

        if (_disposed || version != _backgroundGeneration || !ReferenceEquals(_backgroundIcon, icon) ||
            !ReferenceEquals(_bakingIcon, icon) || Math.Abs(_bakingSide - bakeSide) >= 0.5 || _bakingBlurRadius != blurRadius)
            return;
        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5)
            _bakingIcon = null;
        if (baked == null) return;

        _bakedIcon = icon;
        _bakedBackground = baked;
        _bakedSideDip = bakeSide;
        _bakedBlurRadius = blurRadius;
        if (_backgroundRotationActive)
            BeginBackgroundCrossfade(baked, Math.Max(MainWindow.getDuration(), 1));
    }

    /// <summary>
    /// Fundido real a dos capas: la entrante funde 0 -> enlace sobre la vieja
    /// quieta, y al completar la vieja adopta la imagen nueva y la entrante se
    /// aparca. El compuesto nunca pasa por transparente o negro.
    /// </summary>
    private void BeginBackgroundCrossfade(BitmapSource target, int durationMs)
    {
        if (BackgroundImage.Source == null || !AnimationsEnabled || durationMs <= 1)
        {
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(BackgroundImage.Source, target) && _backgroundCrossfadeTarget == null)
        {
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(_backgroundCrossfadeTarget, target))
            return;

        _backgroundCrossfadeVersion++;
        int version = _backgroundCrossfadeVersion;
        _backgroundCrossfadeTarget = target;
        double bound = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        BackgroundImage.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImage.Opacity = bound;
        BackgroundImageNext.Source = target;
        BackgroundImageNext.Visibility = Visibility.Visible;
        BackgroundImageNext.Opacity = 0;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = bound,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing
        };
        var fadeOut = new DoubleAnimation
        {
            From = bound,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing
        };
        fadeIn.Completed += (_, _) =>
        {
            if (version != _backgroundCrossfadeVersion) return;
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
        };
        BackgroundImage.BeginAnimation(OpacityProperty, fadeOut);
        BackgroundImageNext.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ParkBackgroundNextLayer()
    {
        _backgroundCrossfadeTarget = null;
        double bound = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        BackgroundImage.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImage.Opacity = bound;
        BackgroundImageNext.Visibility = Visibility.Collapsed;
        BackgroundImageNext.Opacity = 0;
    }

    private void CancelBackgroundCrossfade()
    {
        _backgroundCrossfadeVersion++;
        ParkBackgroundNextLayer();
    }

    /// <summary>
    /// El giro solo corre con reproducción activa y la caja a la vista; al
    /// pausar conserva el ángulo para reanudar sin salto (001 RF-19).
    /// </summary>
    private void UpdateRotationPauseState()
    {
        if (!_backgroundRotationActive) return;
        if (!SettingsManager.Current.IslandBackgroundRotate ||
            !SettingsManager.Current.IslandBackgroundBlur)
            return;

        if (_lastStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
            !IsVisible || !IsBoxShown)
            PauseBackgroundRotation();
        else
            ResumeBackgroundRotation();
    }

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

    private void ResumeBackgroundRotation()
    {
        if (!_backgroundRotationPaused) return;
        _backgroundRotationPaused = false;
        ApplyBackgroundRotation(_pausedRotationAngle);
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
        CancelBackgroundCrossfade();
        BackgroundImage.CacheMode = null;
        BackgroundImageNext.CacheMode = null;
        BackgroundImage.RenderTransform = Transform.Identity;
        BackgroundImageNext.RenderTransform = Transform.Identity;
        BackgroundImage.Effect = BackgroundImageBlurEffect;
        BackgroundImageNext.Effect = BackgroundImageNextBlurEffect;
    }
}
