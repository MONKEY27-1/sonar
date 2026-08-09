using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One card for a published "Basic Plugin" (settings pack) in the Marketplace — the
/// no-code counterpart to <see cref="CommunityPluginRowViewModel"/>. Instead of Run, this has
/// Import — applies the pack's settings straight to the current install via
/// <see cref="IPluginPackService"/>, same merge behavior as importing a local .sonarplugin file.</summary>
public sealed partial class CommunityPackRowViewModel : ObservableObject
{
    private readonly CommunityPack _pack;
    private readonly IPluginPackService _pluginPackService;

    public CommunityPackRowViewModel(CommunityPack pack, IPluginPackService pluginPackService)
    {
        _pack = pack;
        _pluginPackService = pluginPackService;
    }

    public string Name => _pack.Name;
    public string Description => _pack.Description ?? string.Empty;
    public string AuthorUsername => _pack.AuthorUsername;
    public bool IsVerified => _pack.IsVerified;

    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatus = string.Empty;

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsImporting) return;

        IsImporting = true;
        try
        {
            await _pluginPackService.ImportAsync(_pack.Pack).ConfigureAwait(true);
            ImportStatus = "Imported.";
        }
        finally
        {
            IsImporting = false;
        }
    }
}
