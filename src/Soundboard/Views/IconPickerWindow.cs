using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Soundboard.Core.Models;

namespace Soundboard.Views;

/// <summary>Small emoji-grid picker for a Voice's icon — built entirely in code-behind, no
/// separate ViewModel, matching InputDialog/PluginTypeChooserWindow's pattern for small one-off
/// dialogs that don't need real data binding.</summary>
public partial class IconPickerWindow : Window
{
    public string? SelectedIcon { get; private set; }

    public IconPickerWindow(string currentIcon)
    {
        Title = "Choose an icon";
        Width = 360;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush")!;

        var heading = new TextBlock
        {
            Text = "Choose an icon",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            Margin = new Thickness(20, 20, 20, 12)
        };

        var wrap = new WrapPanel { Margin = new Thickness(16, 0, 16, 16) };
        foreach (var icon in VoiceIconPalette.Icons)
        {
            var button = new Button
            {
                Content = icon,
                FontSize = 22,
                Width = 48,
                Height = 48,
                Margin = new Thickness(4),
                Style = (Style)Application.Current.FindResource("ToolbarButton")!
            };

            if (icon == currentIcon)
            {
                button.BorderBrush = (Brush)Application.Current.FindResource("AccentBrush")!;
                button.BorderThickness = new Thickness(2);
            }

            button.Click += (_, _) =>
            {
                SelectedIcon = icon;
                DialogResult = true;
            };
            wrap.Children.Add(button);
        }

        var scroll = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new StackPanel();
        root.Children.Add(heading);
        root.Children.Add(scroll);

        Content = root;
    }
}
