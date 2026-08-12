using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Soundboard.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Visibility for "show this group only when the bound enum equals ConverterParameter"
/// — e.g. the Voice Changer's per-effect slider groups, each visible only for its own
/// VoiceEffectType. Compares via ToString() so ConverterParameter can just be a plain string
/// in XAML (e.g. ConverterParameter=Robot) rather than needing an x:Static reference.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Two-way version of <see cref="EnumEqualsConverter"/> for a RadioButton-based
/// segmented control — e.g. the Voice Changer's effect picker, replacing a ComboBox with a row
/// of toggle buttons. Convert: is the bound enum this button's ConverterParameter (drives
/// IsChecked). ConvertBack: clicking a RadioButton to checked=true sets the bound enum to that
/// parameter — WPF only ever calls ConvertBack with true (unchecking one radio button in a
/// group happens by another one becoming checked, not by this one flipping to false), so
/// there's no meaningful "uncheck" case to handle here.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double seconds || seconds < 0) return "0:00";

        // TimeSpan's "m" custom specifier is the minutes-of-the-hour component (0-59), not the
        // total elapsed minutes — for anything an hour or longer that silently dropped the hours
        // entirely (1:05:30 rendered as "5:30"). Only switch to the h:mm:ss form once there
        // actually is an hour to show, so short sounds don't grow a permanent leading "0:".
        var span = TimeSpan.FromSeconds(seconds);
        return span.Hours > 0
            ? span.ToString(@"h\:mm\:ss")
            : span.ToString(@"m\:ss");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Half of a square element's own size, as a CornerRadius — used to make a
/// Border a perfect circle regardless of its (user-configurable) size, by binding this to
/// the Border's own ActualWidth.</summary>
public sealed class SizeToCornerRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double size and > 0 ? new CornerRadius(size / 2) : new CornerRadius(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>An EllipseGeometry matching a square element's own size — used as that element's
/// Clip so child content (progress bar, badges, text) can never visually spill past its
/// circular silhouette into the square's corners.</summary>
public sealed class SizeToEllipseClipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double size || size <= 0) return null;

        var half = size / 2;
        return new EllipseGeometry(new Point(half, half), half, half);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Turns a 0-1 progress value into a star-sized GridLength for a two-column progress
/// track (fill column + remainder column) — scales correctly with the track's actual width,
/// unlike computing a pixel width against some assumed total that stops matching reality the
/// moment the track's real size differs (which it now does per-button, since tiles are
/// user-resizable).  Pass ConverterParameter="remainder" for the second column.</summary>
public sealed class ProgressToStarGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var ratio = string.Equals(parameter as string, "remainder", StringComparison.OrdinalIgnoreCase)
            ? 1 - progress
            : progress;

        return new GridLength(ratio, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex) return Brushes.Transparent;
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PlayingOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.85;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or (string and "") ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Exact inverse of <see cref="NullToCollapsedConverter"/> — visible when null/empty,
/// collapsed otherwise. Used for "no value yet" placeholders (e.g. a default avatar glyph shown
/// only until a real picture is set).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or (string and "") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.4;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PauseGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▶" : "⏸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TestMicLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "⏹ Stop Test" : "🎧 Test Mic";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>Drives a 4-segment password-strength meter: bind each segment's Fill/Background to
/// this converter with the segment's 1-based index as ConverterParameter — it lights up
/// (AccentBrush-ish color scaled by strength) when Score is at least that segment's index.</summary>
public sealed class PasswordStrengthSegmentConverter : IValueConverter
{
    private static readonly Color[] ScoreColors =
    [
        Color.FromRgb(0xEF, 0x44, 0x44), // 1 — weak, danger red
        Color.FromRgb(0xF9, 0x73, 0x16), // 2 — orange
        Color.FromRgb(0xEA, 0xB3, 0x08), // 3 — amber
        Color.FromRgb(0x22, 0xC5, 0x5E), // 4 — strong, success green
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = value is int i ? i : 0;
        var segmentIndex = parameter is string s && int.TryParse(s, out var p) ? p : 0;

        if (segmentIndex < 1 || segmentIndex > ScoreColors.Length || score < segmentIndex)
        {
            return Brushes.Transparent;
        }

        return new SolidColorBrush(ScoreColors[segmentIndex - 1]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
