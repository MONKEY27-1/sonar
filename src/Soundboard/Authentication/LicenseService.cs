using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

public sealed class LicenseService : ILicenseService
{
    public LicenseType CurrentLicense { get; private set; } = LicenseType.Free;
    public bool IsBetaTester { get; private set; }

    // Beta testers and paid Pro users (and Developer/Administrator, which imply Pro) all
    // unlock the same feature set — this is the one place that equivalence is decided.
    public bool IsProUnlocked => CurrentLicense is LicenseType.Pro or LicenseType.Developer or LicenseType.Administrator || IsBetaTester;

    // Free-tier caps.
    private const int FreeMaxSounds = 25;
    private const int FreeMaxFolders = 3;

    public int? MaxSounds => IsProUnlocked ? null : FreeMaxSounds;
    public int? MaxFolders => IsProUnlocked ? null : FreeMaxFolders;
    public bool CanUseCustomTheme => IsProUnlocked;

    public void UpdateFromProfile(UserProfile? profile)
    {
        if (profile is null)
        {
            CurrentLicense = LicenseType.Free;
            IsBetaTester = false;
            return;
        }

        CurrentLicense = profile.License;
        IsBetaTester = profile.IsBetaTester;
    }
}
