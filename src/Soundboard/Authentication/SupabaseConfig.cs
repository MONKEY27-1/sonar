using System.Text.Json;

namespace Soundboard.Authentication;

/// <summary>
/// Supabase project connection info. Defaults to Sonar's own project (baked in below) so the
/// app works out of the box with no setup step — the anon/public key is safe to ship in a
/// client app by design (Supabase enforces actual data access via Row Level Security policies
/// on the server side, not by hiding this key). A local supabase-config.json next to the exe
/// still overrides these, for pointing a dev build at a different (e.g. staging) project.
/// </summary>
public sealed class SupabaseConfig
{
    private const string DefaultProjectUrl = "https://zagelzxqgandqtmndswa.supabase.co";
    private const string DefaultAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InphZ2VsenhxZ2FuZHF0bW5kc3dhIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODU3MjA3OTMsImV4cCI6MjEwMTI5Njc5M30.Uhh3koGFJe5UxfMZY99o87l55G_NOWbzuN4_PIrTI-8";

    public string ProjectUrl { get; set; } = DefaultProjectUrl;
    public string AnonKey { get; set; } = DefaultAnonKey;

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
            var defaults = new SupabaseConfig();
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            }
            catch
            {
                // Best-effort — if we can't write the file, Load() still returns the baked-in
                // defaults, so the app is configured either way.
            }

            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<SupabaseConfig>(json, JsonOptions);

            // A leftover blank placeholder from an older build shouldn't leave the app stuck
            // unconfigured — fall back to the baked-in defaults. A deliberately-edited file
            // pointing at a different project (IsConfigured == true) is still honored as-is.
            return loaded is { IsConfigured: true } ? loaded : new SupabaseConfig();
        }
        catch
        {
            return new SupabaseConfig();
        }
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "supabase-config.json");
}
