using System.Text.Json;

namespace Soundboard.Authentication;

/// <summary>
/// Supabase project connection info. Loaded from a local JSON file next to the exe
/// (supabase-config.json) rather than hardcoded — never commit real credentials to source.
/// The anon/public key is safe to ship in a client app by design (Supabase enforces actual
/// data access via Row Level Security policies on the server side, not by hiding this key).
/// </summary>
public sealed class SupabaseConfig
{
    public string ProjectUrl { get; set; } = string.Empty;
    public string AnonKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectUrl) && !string.IsNullOrWhiteSpace(AnonKey);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static SupabaseConfig Load()
    {
        var path = ConfigPath;

        if (!File.Exists(path))
        {
            var placeholder = new SupabaseConfig();
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(placeholder, JsonOptions));
            }
            catch
            {
                // Best-effort — if we can't write a placeholder, Load() still returns an
                // unconfigured instance and the app runs in offline-only mode.
            }

            return placeholder;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SupabaseConfig>(json, JsonOptions) ?? new SupabaseConfig();
        }
        catch
        {
            return new SupabaseConfig();
        }
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "supabase-config.json");
}
