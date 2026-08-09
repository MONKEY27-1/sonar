using System.Text.RegularExpressions;
using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

/// <summary>Simple word-boundary blocklist check — deliberately not a substring match (which
/// would false-positive on innocent words like "assassin" or "classic"), and deliberately not an
/// external moderation API (keeps this offline, transparent, and easy to extend by just editing
/// the list below). This is a convenience filter on the client; the actual safety backstop is
/// that every public submission still requires admin verification before it's marked trustworthy
/// (see IAdminService.SetCommunityPluginVerifiedAsync/SetCommunityPackVerifiedAsync) — a modified
/// client could bypass this check, same caveat as every other client-side-only gate in this app.</summary>
public sealed partial class ProfanityFilterService : IProfanityFilterService
{
    // Exact-word matches only (no prefix matching) — a prefix check would false-positive on
    // innocent words like "assassin"/"classic" containing a blocked root as a substring, so
    // common inflections (fucking, shitty, ...) are listed explicitly instead.
    private static readonly HashSet<string> BlockedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fuck", "fucks", "fucking", "fucked", "fucker", "motherfucker",
        "shit", "shits", "shitty", "shitting", "bullshit",
        "bitch", "bitches", "bitching",
        "cunt", "cunts",
        "asshole", "assholes",
        "bastard", "bastards",
        "dick", "dicks",
        "piss", "pissed", "pissing",
        "nigger", "niggers", "nigga", "niggas",
        "faggot", "faggots", "fag", "fags",
        "retard", "retarded", "retards",
        "whore", "whores",
        "slut", "sluts",
        "rape", "raped", "raping", "rapist",
        "nazi", "nazis",
        "kike", "kikes", "spic", "spics", "chink", "chinks", "tranny", "trannies", "coon", "coons"
    };

    public bool ContainsProfanity(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (Match match in WordPattern().Matches(text))
        {
            if (BlockedWords.Contains(match.Value)) return true;
        }

        return false;
    }

    [GeneratedRegex(@"[a-zA-Z']+")]
    private static partial Regex WordPattern();
}
