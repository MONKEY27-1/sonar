namespace Soundboard.Core.Models;

/// <summary>The fixed set of icons a Voice can show in the Voicemod-style tile grid, plus a
/// deterministic default picker (hashed from a Voice's Id) so a freshly created voice starts
/// with some visual variety before the user bothers to customize it via the icon picker.</summary>
public static class VoiceIconPalette
{
    public static readonly string[] Icons =
    [
        "🎤", "🎭", "🤖", "👽", "🐻", "🎃", "👻", "🦊", "🧙", "🐸", "🦇", "🐺", "🦁", "🐉", "👹", "🎅"
    ];

    public static string PickDefault(string id)
    {
        if (string.IsNullOrEmpty(id)) return Icons[0];

        var hash = 0;
        foreach (var c in id)
        {
            hash = hash * 31 + c;
        }

        return Icons[Math.Abs(hash) % Icons.Length];
    }
}
