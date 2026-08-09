using CommunityToolkit.Mvvm.ComponentModel;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One editable row in the Admin Panel's Community Packs grid — mirrors
/// <see cref="AdminCommunityPluginRowViewModel"/> for published "Basic Plugin" settings packs.</summary>
public partial class AdminCommunityPackRowViewModel : ObservableObject
{
    public AdminCommunityPackRowViewModel(CommunityPack pack)
    {
        Id = pack.Id;
        Name = pack.Name;
        AuthorUsername = pack.AuthorUsername;

        var included = new List<string>();
        if (pack.Pack.Hotkeys is not null) included.Add("hotkeys");
        if (pack.Pack.VoiceChangerPresets is not null) included.Add("presets");
        if (pack.Pack.Theme is not null) included.Add("theme");
        Contents = included.Count > 0 ? string.Join(", ", included) : "(empty)";

        _isVerified = pack.IsVerified;
    }

    public string Id { get; }
    public string Name { get; }
    public string AuthorUsername { get; }
    public string Contents { get; }

    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
}
