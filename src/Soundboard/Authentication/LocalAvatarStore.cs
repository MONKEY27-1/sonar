using System.Text.Json;
using Soundboard.Core.Interfaces;

namespace Soundboard.Authentication;

/// <summary>
/// Profile pictures are stored locally only — no Supabase Storage bucket/policies exist yet,
/// so avatars don't sync across devices (same "cloud-ready, not implemented" boundary as
/// <see cref="ICloudService"/>). Files live under <see cref="IAppPaths.ProfilesDirectory"/>,
/// keyed by user id via a small JSON manifest alongside them.
/// </summary>
public sealed class LocalAvatarStore
{
    private readonly IAppPaths _paths;
    private readonly string _indexFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LocalAvatarStore(IAppPaths paths)
    {
        _paths = paths;
        _indexFile = Path.Combine(_paths.ProfilesDirectory, "avatars.json");
    }

    public string? GetAvatarPath(string userId)
    {
        var index = LoadIndex();
        if (!index.TryGetValue(userId, out var fileName)) return null;

        var path = Path.Combine(_paths.ProfilesDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Copies the picked file into local app storage and returns its new path, or null
    /// if the copy failed (a bad picture shouldn't be a fatal error — the profile just keeps
    /// whatever avatar it had before).</summary>
    public string? SetAvatar(string userId, string sourceFilePath)
    {
        try
        {
            var extension = Path.GetExtension(sourceFilePath);
            var fileName = $"avatar_{userId}{extension}";
            var destinationPath = Path.Combine(_paths.ProfilesDirectory, fileName);

            File.Copy(sourceFilePath, destinationPath, overwrite: true);

            var index = LoadIndex();
            index[userId] = fileName;
            SaveIndex(index);

            return destinationPath;
        }
        catch
        {
            return null;
        }
    }

    public void ClearAvatar(string userId)
    {
        try
        {
            var index = LoadIndex();
            if (index.Remove(userId, out var fileName))
            {
                var path = Path.Combine(_paths.ProfilesDirectory, fileName);
                if (File.Exists(path)) File.Delete(path);
                SaveIndex(index);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private Dictionary<string, string> LoadIndex()
    {
        try
        {
            if (!File.Exists(_indexFile)) return new();
            var json = File.ReadAllText(_indexFile);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void SaveIndex(Dictionary<string, string> index)
    {
        try
        {
            File.WriteAllText(_indexFile, JsonSerializer.Serialize(index, JsonOptions));
        }
        catch
        {
            // Best-effort — an unwritten index just means the avatar has to be re-picked.
        }
    }
}
