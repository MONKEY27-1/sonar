using CommunityToolkit.Mvvm.ComponentModel;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One editable row in the Admin Panel's Community Plugins grid — wraps a fetched
/// <see cref="CommunityPlugin"/> with the one mutable field an admin can change (IsVerified) plus
/// per-row save/delete state, same shape as <see cref="PluginTrustRowViewModel"/>.</summary>
public partial class AdminCommunityPluginRowViewModel : ObservableObject
{
    private const int PreviewLength = 80;

    public AdminCommunityPluginRowViewModel(CommunityPlugin plugin)
    {
        Id = plugin.Id;
        Name = plugin.Name;
        AuthorUsername = plugin.AuthorUsername;
        ScriptPreview = plugin.ScriptSource.Length > PreviewLength
            ? plugin.ScriptSource[..PreviewLength] + "…"
            : plugin.ScriptSource;
        _isVerified = plugin.IsVerified;
    }

    public string Id { get; }
    public string Name { get; }
    public string AuthorUsername { get; }
    public string ScriptPreview { get; }

    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
}
