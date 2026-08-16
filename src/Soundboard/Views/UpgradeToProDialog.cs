using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soundboard.Views;

/// <summary>Shown at the moment a Free-tier user actually hits a wall — the sound/folder cap, or
/// a locked Marketplace plugin — rather than just leaving them with a status-bar message. Same
/// plain code-behind Window pattern as InputDialog/HotkeyCaptureDialog (no dialog abstraction).</summary>
public sealed class UpgradeToProDialog : Window
{
    // Keep in sync with the identical constant in SettingsViewModel.cs.
    private const string PricingPageUrl = "https://sonars.netlify.app/index.html#tiers";


    public UpgradeToProDialog(string reason, string detail)
    {
        Title = "Upgrade to Pro";
        Width = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Background = (Brush)Application.Current.FindResource("SurfaceBrush")!;

        var textBrush = (Brush)Application.Current.FindResource("TextPrimaryBrush")!;
        var accentBrush = (Brush)Application.Current.FindResource("AccentBrush")!;
        var accentButtonStyle = (Style)Application.Current.FindResource("AccentButton")!;
        var toolbarButtonStyle = (Style)Application.Current.FindResource("ToolbarButton")!;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = reason,
            Foreground = textBrush,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        root.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = textBrush,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var perksHeader = new TextBlock
        {
            Text = "PRO INCLUDES",
            Foreground = textBrush,
            Opacity = 0.6,
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(perksHeader);

        string[] perks =
        [
            "Unlimited sounds & folders",
            "Voice Changer",
            "Custom themes",
            "Cloud Sync across devices",
            "Performance Mode & Advanced Settings"
        ];
        foreach (var perk in perks)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = "✓", Foreground = accentBrush, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = perk, Foreground = textBrush });
            root.Children.Add(row);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };

        var laterButton = new Button { Content = "Maybe Later", Style = toolbarButtonStyle, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var upgradeButton = new Button { Content = "Upgrade to Pro", Style = accentButtonStyle, IsDefault = true };
        upgradeButton.Click += (_, _) =>
        {
            // Same "open a URL, nothing to recover if it fails" pattern as
            // FirstRunWizardViewModel.InstallVbCable — WPF has no embedded Stripe Checkout
            // surface, so purchasing happens on the website in the user's default browser.
            try
            {
                Process.Start(new ProcessStartInfo(PricingPageUrl) { UseShellExecute = true });
            }
            catch
            {
                // Nothing to recover — the user can navigate to the site manually.
            }

            Close();
        };

        buttons.Children.Add(laterButton);
        buttons.Children.Add(upgradeButton);
        root.Children.Add(buttons);

        Content = root;
    }
}
