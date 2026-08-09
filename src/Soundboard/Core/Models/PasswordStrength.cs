namespace Soundboard.Core.Models;

/// <summary>Result of evaluating a candidate password's strength, for the register/reset/change
/// password screens' strength meter.</summary>
public sealed class PasswordStrengthResult
{
    /// <summary>0 (empty/very weak) through 4 (strong).</summary>
    public int Score { get; init; }
    public string Label { get; init; } = string.Empty;

    public static readonly PasswordStrengthResult Empty = new() { Score = 0, Label = string.Empty };
}
