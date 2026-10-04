// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FluentFlyout.Controls.TaskbarWidget;

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
