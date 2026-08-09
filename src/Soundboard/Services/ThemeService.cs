using System.Windows;
using System.Windows.Media;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

public sealed class ThemeService : IThemeService
{
    public event EventHandler? ThemeChanged;

    public void ApplyTheme(AppSettings settings)
    {
        var theme = settings.Theme;
        var resources = Application.Current.Resources;

        var (background, surface, text, accent) = theme.Kind switch
        {
            ThemeKind.Light => ("#F8FAFC", "#FFFFFF", "#0F172A", theme.AccentColor),
            ThemeKind.Amoled => ("#000000", "#0A0A0A", "#FFFFFF", theme.AccentColor),
            ThemeKind.Custom => (theme.BackgroundColor, theme.SurfaceColor, theme.TextColor, theme.AccentColor),
            _ => ("#0F172A", "#1E293B", "#F8FAFC", theme.AccentColor)
        };

        SetBrush(resources, "BackgroundBrush", background);
        SetBrush(resources, "SurfaceBrush", surface);
        SetBrush(resources, "SurfaceElevatedBrush", Lighten(surface, 0.08));
        SetBrush(resources, "TextPrimaryBrush", text);
        SetBrush(resources, "TextSecondaryBrush", Lighten(text, theme.Kind == ThemeKind.Light ? -0.35 : 0.35));
        SetBrush(resources, "AccentBrush", accent);
        SetBrush(resources, "AccentHoverBrush", Lighten(accent, 0.12));
        SetBrush(resources, "BorderBrush", Lighten(surface, theme.Kind == ThemeKind.Light ? -0.08 : 0.12));
        SetBrush(resources, "DangerBrush", "#EF4444");
        SetBrush(resources, "SuccessBrush", "#22C55E");

        resources["AppFontFamily"] = new FontFamily(theme.FontFamily);
        resources["AppFontSize"] = theme.FontSize;
        resources["ButtonSize"] = theme.ButtonSize;
        resources["ButtonSpacing"] = theme.ButtonSpacing;
        resources["ButtonSpacingThickness"] = new Thickness(theme.ButtonSpacing);
        resources["CornerRadius"] = new CornerRadius(theme.CornerRadius);

        if (Application.Current.MainWindow is not null)
        {
            Application.Current.MainWindow.Opacity = theme.WindowOpacity;
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = new SolidColorBrush(ParseColorOrFallback(hex, key));
    }

    private static string Lighten(string hex, double amount)
    {
        var color = ParseColorOrFallback(hex, "Lighten");
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R + amount * 255, 0, 255),
            (byte)Math.Clamp(color.G + amount * 255, 0, 255),
            (byte)Math.Clamp(color.B + amount * 255, 0, 255)).ToString();
    }

    /// <summary>
    /// Parses a hex color string, falling back to a safe default instead of throwing on
    /// invalid input. Theme colors are user-editable free-text (the Appearance tab's hex
    /// textboxes have no format validation), so a single bad value must never be able to
    /// throw mid-way through ApplyTheme — that would leave whichever resources hadn't been
    /// set yet (previously, anything after the crash point, worst case TextPrimaryBrush
    /// itself) at WPF's own default instead of a theme color, which against a dark background
    /// looks like text having silently vanished rather than an obvious error.
    /// </summary>
    private static Color ParseColorOrFallback(string? hex, string key)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color color)
                {
                    return color;
                }
            }
            catch (FormatException)
            {
                // Fall through to the safe default below.
            }
        }

        // Text/background fallbacks are chosen to stay legible against each other even in
        // the worst case (falling back for both at once still yields readable contrast).
        return key.Contains("Text", StringComparison.OrdinalIgnoreCase)
            ? Colors.White
            : (Color)ColorConverter.ConvertFromString("#0F172A")!;
    }
}
