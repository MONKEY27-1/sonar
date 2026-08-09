using CommunityToolkit.Mvvm.ComponentModel;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One editable row in the Admin Panel's Plugins grid — wraps a <see cref="PluginDefinition"/>
/// (immutable, from the local catalog) with the one mutable field an admin can change
/// (IsVerified) plus per-row save state, same shape as <see cref="AdminUserRowViewModel"/>.</summary>
public partial class PluginTrustRowViewModel : ObservableObject
{
    public PluginTrustRowViewModel(PluginDefinition definition, bool isVerified)
    {
        Id = definition.Id;
        Name = definition.Name;
        Author = definition.Author;
        _isVerified = isVerified;
    }

    public string Id { get; }
    public string Name { get; }
    public string Author { get; }

    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
}
