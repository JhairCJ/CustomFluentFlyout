using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Controls;

public sealed partial class SettingsExpander : UserControl
{
    public SettingsExpander()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsExpander), new PropertyMetadata(string.Empty, OnTitleChanged));

    public object? ExpanderContent
    {
        get => GetValue(ExpanderContentProperty);
        set => SetValue(ExpanderContentProperty, value);
    }

    public static readonly DependencyProperty ExpanderContentProperty =
        DependencyProperty.Register(nameof(ExpanderContent), typeof(object), typeof(SettingsExpander), new PropertyMetadata(null, OnContentChanged));

    private static void OnTitleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SettingsExpander expander)
            expander.TitleText.Text = expander.Title;
    }

    private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SettingsExpander expander)
            expander.ContentHost.Content = expander.ExpanderContent;
    }

    private void HeaderButton_Click(object sender, RoutedEventArgs e)
    {
        ContentHost.Visibility = ContentHost.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
