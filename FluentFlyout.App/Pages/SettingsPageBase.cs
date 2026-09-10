using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Pages;

public abstract class SettingsPageBase : Page
{
    protected static StackPanel CreatePage(string eyebrow, string title, string description)
    {
        return new StackPanel
        {
            Spacing = 12,
            Margin = new Microsoft.UI.Xaml.Thickness(32, 28, 40, 40),
            Children =
            {
                new TextBlock { Text = eyebrow.ToUpperInvariant(), FontSize = 11, CharacterSpacing = 140, Opacity = 0.7 },
                new TextBlock { Text = title, FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = description, FontSize = 14, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Opacity = 0.75 }
            }
        };
    }
}
