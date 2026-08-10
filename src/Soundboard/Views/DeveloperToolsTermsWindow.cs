using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soundboard.Views;

/// <summary>Shown once, the first time a user installs the Developer plugin (unlocks the Basic/
/// Custom Plugin authoring tools) — gated by AppSettings.PluginSettings.
/// HasAcceptedDeveloperToolsTerms so it doesn't nag on every reinstall. Built entirely in
/// code-behind, no separate ViewModel, matching InputDialog/PluginTypeChooserWindow's pattern for
/// small one-off dialogs that don't need real data binding.</summary>
public partial class DeveloperToolsTermsWindow : Window
{
    private const string TermsText =
        "Developer Tools unlock Sonar's plugin authoring tools — the Basic Plugin packager and " +
        "the sandboxed JavaScript Custom Plugin editor. Before you install:\n\n" +
        "• Custom Plugin scripts run inside a restricted sandbox (no file system, network, or " +
        "system access) — but you're responsible for what your own scripts do within Sonar.\n\n" +
        "• If you publish a plugin or pack publicly, your username is permanently attached to it " +
        "as the author, and it becomes visible to every Sonar user.\n\n" +
        "• Published content must not contain malicious code, profanity, or attempts to break " +
        "out of the sandbox. Sonar admins may unverify or delete any published content at their " +
        "discretion, including content previously approved.\n\n" +
        "• Unverified content hasn't been reviewed by an admin — install other people's " +
        "unverified plugins at your own judgment.\n\n" +
        "• Sonar and its admins aren't liable for anything a script you write, install, or run " +
        "does within your own soundboard.";

    public DeveloperToolsTermsWindow()
    {
        Title = "Developer Tools — Terms of Use";
        Width = 520;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush")!;

        var heading = new TextBlock
        {
            Text = "Before you install Developer Tools",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            Margin = new Thickness(24, 24, 24, 12),
            TextWrapping = TextWrapping.Wrap
        };

        var termsBlock = new TextBlock
        {
            Text = TermsText,
            FontSize = 12,
            Opacity = 0.85,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            TextWrapping = TextWrapping.Wrap
        };

        var scrollViewer = new ScrollViewer
        {
            Content = termsBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(24, 0, 24, 16),
            Height = 300
        };

        var agreeCheckBox = new CheckBox
        {
            Content = "I have read and agree to these terms",
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush")!,
            Margin = new Thickness(24, 0, 24, 16)
        };

        var acceptButton = new Button
        {
            Content = "Accept",
            Style = (Style)Application.Current.FindResource("AccentButton")!,
            Width = 100,
            Margin = new Thickness(0, 0, 8, 24),
            IsEnabled = false
        };

        var declineButton = new Button
        {
            Content = "Decline",
            Style = (Style)Application.Current.FindResource("ToolbarButton")!,
            Width = 100,
            Margin = new Thickness(0, 0, 24, 24),
            IsCancel = true
        };

        agreeCheckBox.Checked += (_, _) => acceptButton.IsEnabled = true;
        agreeCheckBox.Unchecked += (_, _) => acceptButton.IsEnabled = false;

        acceptButton.Click += (_, _) => DialogResult = true;
        declineButton.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(acceptButton);
        buttons.Children.Add(declineButton);

        var root = new StackPanel();
        root.Children.Add(heading);
        root.Children.Add(scrollViewer);
        root.Children.Add(agreeCheckBox);
        root.Children.Add(buttons);

        Content = root;
    }
}
