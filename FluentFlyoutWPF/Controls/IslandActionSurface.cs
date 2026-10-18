using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentFlyoutWPF.Controls;

/// <summary>A lightweight action surface with the same keyboard and automation contract as a button.</summary>
public sealed class IslandActionSurface : Border
{
    public event RoutedEventHandler? Activated;
    private RectangleGeometry? _contentClip;
    private Rect _contentBounds;
    private double _contentRadius = double.NaN;

    public IslandActionSurface()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    internal void Invoke()
    {
        if (IsEnabled) Activated?.Invoke(this, new RoutedEventArgs());
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Size result = base.ArrangeOverride(arrangeSize);
        UpdateChildClip();
        return result;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty || e.Property == BorderThicknessProperty) UpdateChildClip();
    }

    private void UpdateChildClip()
    {
        if (Child == null) return;
        double radius = Math.Max(0, CornerRadius.TopLeft - BorderThickness.Left);
        var bounds = new Rect(Child.RenderSize);
        if (_contentClip == null || bounds != _contentBounds || radius != _contentRadius)
        {
            _contentBounds = bounds;
            _contentRadius = radius;
            _contentClip = new RectangleGeometry(bounds, radius, radius);
            _contentClip.Freeze();
        }
        if (!ReferenceEquals(Child.Clip, _contentClip)) Child.Clip = _contentClip;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key is Key.Enter or Key.Space)
        {
            Invoke();
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ActionPeer(this);

    private sealed class ActionPeer(IslandActionSurface owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(IslandActionSurface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        void IInvokeProvider.Invoke()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(new Action(owner.Invoke));
        }
    }
}
