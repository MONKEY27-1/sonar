using System.Text.RegularExpressions;

namespace Soundboard.Helpers;

/// <summary>Client-side input validation for the auth screens. This is a first line of defense
/// for UX (instant inline feedback) only — the server (Supabase Auth / the profiles table
/// constraints) is the actual source of truth for uniqueness and password policy.</summary>
public static partial class InputValidators
{
    public const string PasswordRequirementsText = "At least 8 characters.";

    public static bool IsValidUsername(string? username)
        => username is { Length: >= 3 and <= 20 } && UsernamePattern().IsMatch(username);

    public static bool IsValidEmail(string? email)
        => !string.IsNullOrWhiteSpace(email) && EmailPattern().IsMatch(email);

    public static bool IsValidPassword(string? password)
        => password is { Length: >= 8 };

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$")]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
