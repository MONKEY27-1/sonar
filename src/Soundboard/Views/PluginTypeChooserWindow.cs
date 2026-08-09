using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soundboard.Views;

public enum PluginCreationType
{
    Basic,
    Custom
}

/// <summary>Tiny picker shown when "Create a Plugin" is clicked — routes to either the settings-pack
/// authoring window (Basic) or the sandboxed-script authoring window (Custom). Built entirely in
/// code-behind, no separate ViewModel, matching InputDialog's pattern for small one-off dialogs
/// that don't need real data binding.</summary>
public partial class PluginTypeChooserWindow : Window
{
    public PluginCreationType? SelectedType { get; private set; }

    public PluginTypeChooserWindow()
    {
        Title = "Create a Plugin";
        Width = 460;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush")!;

        var heading = new TextBlock
        {
            Text = "What kind of plugin do you want to create?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            Margin = new Thickness(24, 24, 24, 16),
            TextWrapping = TextWrapping.Wrap
        };

        var basicButton = CreateOptionButton(
            "📦 Basic Plugin",
            "Package your hotkeys, voice changer presets, and theme into a shareable file. No code.");
        var customButton = CreateOptionButton(
            "👨‍💻 Custom Plugin",
            "Write a small sandboxed script and publish it for everyone to see and run.");

        basicButton.Click += (_, _) =>
        {
            SelectedType = PluginCreationType.Basic;
            DialogResult = true;
        };
        customButton.Click += (_, _) =>
        {
            SelectedType = PluginCreationType.Custom;
            DialogResult = true;
        };

        var optionsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        optionsPanel.Children.Add(basicButton);
        optionsPanel.Children.Add(customButton);

        var root = new StackPanel();
        root.Children.Add(heading);
        root.Children.Add(optionsPanel);

        Content = root;
    }

    private static Button CreateOptionButton(string title, string description)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };

        var descriptionBlock = new TextBlock
        {
            Text = description,
            FontSize = 11,
            Opacity = 0.7,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            TextWrapping = TextWrapping.Wrap
        };

        var content = new StackPanel();
        content.Children.Add(titleBlock);
        content.Children.Add(descriptionBlock);

        return new Button
        {
            Content = content,
            Style = (Style)Application.Current.FindResource("ToolbarButton")!,
            Width = 180,
            Height = 150,
            Margin = new Thickness(8, 0, 8, 24),
            Padding = new Thickness(14),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top
        };
    }
}
