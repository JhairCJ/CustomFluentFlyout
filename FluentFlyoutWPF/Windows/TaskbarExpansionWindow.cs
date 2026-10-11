using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FluentFlyoutWPF.Classes;

namespace FluentFlyout.Windows;

/// <summary>Independent popup; the compact widget's HWND remains an Explorer child.</summary>
internal sealed class TaskbarExpansionWindow : Window
{
    internal Canvas Surface { get; } = new();
    internal TaskbarExpansionWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Content = Surface;
        WindowHelper.SetNoActivate(this);
        new WindowInteropHelper(this).EnsureHandle();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelper.ExcludeFromPeek(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            return IntPtr.Zero;
        });
    }
}
