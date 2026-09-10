using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Controls;

public sealed partial class SettingsCard : UserControl
{
    public SettingsCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, OnTextChanged));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, OnTextChanged));

    public object? CardContent
    {
        get => GetValue(CardContentProperty);
        set => SetValue(CardContentProperty, value);
    }

    public static readonly DependencyProperty CardContentProperty =
        DependencyProperty.Register(nameof(CardContent), typeof(object), typeof(SettingsCard), new PropertyMetadata(null, OnContentChanged));

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SettingsCard card)
        {
            card.TitleText.Text = card.Title;
            card.DescriptionText.Text = card.Description;
        }
    }

    private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SettingsCard card)
            card.ContentHost.Content = card.CardContent;
    }
}
