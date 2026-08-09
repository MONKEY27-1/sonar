namespace Soundboard.Core.Models;

public static class LicenseTypeExtensions
{
    /// <summary>Parses the "license" text column from Supabase (e.g. "Free", "BetaTester")
    /// into a <see cref="LicenseType"/>, defaulting to Free for anything unrecognized rather
    /// than throwing — an unrecognized value should never accidentally unlock paid features.</summary>
    public static LicenseType ParseOrFree(this string? value)
        => Enum.TryParse<LicenseType>(value, ignoreCase: true, out var license) ? license : LicenseType.Free;
}
