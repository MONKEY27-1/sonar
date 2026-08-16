using System.Windows.Input;
using Soundboard.Core.Models;

namespace Soundboard.Helpers;

public enum HotkeyCaptureOutcome
{
    /// <summary>A bare modifier press — not a complete binding on its own, keep waiting.</summary>
    StillWaiting,
    Cancelled,
    Captured
}

/// <summary>Shared key-capture logic for hotkey recording — previously duplicated near-verbatim
/// between the Settings window's inline capture buttons and HotkeyCaptureDialog's popup. Only the
/// capture rules live here; what each caller does with the result (assign to a settings slot vs.
/// a dialog's Result property, update a Button vs. a whole dialog) stays call-site-specific.</summary>
public static class HotkeyCaptureHelper
{
    public static HotkeyCaptureOutcome TryCapture(KeyEventArgs e, out HotkeyBinding? binding)
    {
        e.Handled = true;
        binding = null;

        if (e.Key == Key.Escape)
        {
            return HotkeyCaptureOutcome.Cancelled;
        }

        // Bare modifier presses (and the "System" pseudo-key WPF uses for Alt combos)
        // aren't a complete binding on their own — keep waiting for the actual key.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            return HotkeyCaptureOutcome.StillWaiting;
        }

        binding = new HotkeyBinding
        {
            KeyCode = KeyInterop.VirtualKeyFromKey(key),
            Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
            Shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
        };
        return HotkeyCaptureOutcome.Captured;
    }
}
