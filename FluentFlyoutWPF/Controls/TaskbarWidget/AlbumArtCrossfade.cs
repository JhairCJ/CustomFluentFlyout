// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using FluentFlyout.Classes.Utils;

namespace FluentFlyout.Controls.TaskbarWidget;

/// <summary>One transition lifecycle for the widget and both island cover surfaces.</summary>
internal sealed class AlbumArtTransition(Action<BitmapImage?> apply,
    params (Border Surface, ScaleTransform Scale)[] surfaces)
{
    private AlbumArtCrossfade[]? _fades;
    private BitmapImage? _visible;
    private int _version;
    private bool _flipping, _hasPresentedArtwork, _preferFade;
    private double _durationMs;
    public BitmapImage? Target { get; private set; }
    private bool IsRunning => _flipping || _fades?.Any(fade => fade.IsRunning) == true;

    public void Set(BitmapImage? art)
    {
        Stop();
        Target = art;
        Apply(art);
    }

    public void Show(BitmapImage? art, bool animated, double durationMs, bool preferFade)
    {
        if (!animated) { Set(art); return; }
        if (ReferenceEquals(art, Target) && IsRunning) return;
        Target = art;
        _durationMs = durationMs;
        _preferFade = preferFade;
        if (_flipping) return;
        var transition = AlbumArtworkSimilarity.ChooseTransition(_visible, art, _hasPresentedArtwork, preferFade);
        if (transition == AlbumArtworkTransition.Direct) { Set(art); return; }
        if (transition == AlbumArtworkTransition.Fade)
        {
            ResetFlip();
            _fades ??= surfaces.Select(surface => new AlbumArtCrossfade(surface.Surface)).ToArray();
            // Capture every outgoing surface before the shared artwork swap.
            for (int i = 0; i < _fades.Length; i++)
                _fades[i].Fade(i == _fades.Length - 1 ? () => Apply(art) : () => { }, durationMs);
            return;
        }

        Stop();
        _flipping = true;
        int version = _version;
        double halfMs = Math.Clamp(durationMs * 0.35, 90, 200);
        var outgoing = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(halfMs))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        outgoing.Completed += (_, _) =>
        {
            if (version != _version) return;
            Apply(Target);
            var incoming = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(halfMs))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            incoming.Completed += (_, _) =>
            {
                if (version != _version) return;
                ResetFlip();
                if (!ReferenceEquals(Target, _visible)) Show(Target, true, _durationMs, _preferFade);
            };
            Animate(incoming);
        };
        Animate(outgoing);
    }

    public void Stop()
    {
        ResetFlip();
        if (_fades != null) foreach (var fade in _fades) fade.Stop();
    }

    private void ResetFlip()
    {
        ++_version;
        _flipping = false;
        foreach (var (_, scale) in surfaces)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
        }
    }

    private void Animate(DoubleAnimation animation)
    {
        // Only the first surface owns completion callbacks; all others share its timing.
        for (int i = surfaces.Length - 1; i >= 0; i--)
            surfaces[i].Scale.BeginAnimation(ScaleTransform.ScaleXProperty, i == 0 ? animation : animation.Clone());
    }

    private void Apply(BitmapImage? art)
    {
        _visible = art;
        if (art != null) _hasPresentedArtwork = true;
        apply(art);
    }
}

/// <summary>A small artwork-only snapshot dissolves over the live incoming cover.</summary>
internal sealed class AlbumArtCrossfade
{
    private readonly Border _surface;
    private readonly Image _outgoing = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private int _version;
    public bool IsRunning => _outgoing.Visibility == Visibility.Visible;

    public AlbumArtCrossfade(Border surface)
    {
        _surface = surface;
        if (surface.Child is not Grid grid)
        {
            var child = surface.Child;
            surface.Child = null;
            grid = new Grid();
            if (child != null) grid.Children.Add(child);
            surface.Child = grid;
        }
        Panel.SetZIndex(_outgoing, 100);
        grid.Children.Add(_outgoing);
    }

    public void Fade(Action update, double durationMs)
    {
        BitmapSource? snapshot = null;
        if (CanCapture() && _surface.ActualWidth > 0 && _surface.ActualHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(_surface);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(_surface), null,
                    new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(_surface.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(_surface.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            snapshot = bitmap;
        }
        // Capturing before stopping preserves the current composite on rapid skips.
        Stop();
        update();
        if (snapshot == null) return;
        _outgoing.Source = snapshot;
        _outgoing.Visibility = Visibility.Visible;
        _outgoing.Opacity = 1;
        int version = _version;
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(durationMs))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) => { if (version == _version) Stop(); };
        _outgoing.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private bool CanCapture()
    {
        if (!_surface.IsVisible) return false;
        // Island layers remain measurable while their parent's opacity hides them.
        for (DependencyObject? node = _surface; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Opacity <= 0) return false;
        return true;
    }

    public void Stop()
    {
        ++_version;
        _outgoing.BeginAnimation(UIElement.OpacityProperty, null);
        _outgoing.Visibility = Visibility.Collapsed;
        _outgoing.Source = null;
    }
}
