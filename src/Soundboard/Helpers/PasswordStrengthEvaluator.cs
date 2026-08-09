using System.Text.RegularExpressions;
using Soundboard.Core.Models;

namespace Soundboard.Helpers;

/// <summary>Simple, dependency-free password strength heuristic for the register/reset/change
/// password screens' strength meter. Not a substitute for server-side validation — Supabase
/// still enforces its own minimum (8 characters) independently.</summary>
public static partial class PasswordStrengthEvaluator
{
    public static PasswordStrengthResult Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordStrengthResult.Empty;
        }

        var score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        if (LowerAndUpper().IsMatch(password)) score++;
        if (HasDigit().IsMatch(password) && HasSymbol().IsMatch(password)) score++;

        var label = score switch
        {
            0 => "Very weak",
            1 => "Weak",
            2 => "Fair",
            3 => "Good",
            _ => "Strong"
        };

        return new PasswordStrengthResult { Score = score, Label = label };
    }

    [GeneratedRegex(@"(?=.*[a-z])(?=.*[A-Z])")]
    private static partial Regex LowerAndUpper();

    [GeneratedRegex(@"\d")]
    private static partial Regex HasDigit();

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex HasSymbol();
}
