using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Views;

namespace Soundboard.ViewModels;

/// <summary>One card for a published "Basic Plugin" (settings pack) in the Marketplace — the
/// no-code counterpart to <see cref="CommunityPluginRowViewModel"/>. Instead of Run, this has
/// Import — applies the pack's settings straight to the current install via
/// <see cref="IPluginPackService"/>, same merge behavior as importing a local .sonarplugin file.</summary>
public sealed partial class CommunityPackRowViewModel : ObservableObject
{
    private readonly CommunityPack _pack;
    private readonly IPluginPackService _pluginPackService;
    private readonly ISessionService _sessionService;
    private readonly IContentReportService _reportService;

    public CommunityPackRowViewModel(CommunityPack pack, IPluginPackService pluginPackService, ISessionService sessionService, IContentReportService reportService)
    {
        _pack = pack;
        _pluginPackService = pluginPackService;
        _sessionService = sessionService;
        _reportService = reportService;
    }

    public string Name => _pack.Name;
    public string Description => _pack.Description ?? string.Empty;
    public string AuthorUsername => _pack.AuthorUsername;
    public bool IsVerified => _pack.IsVerified;

    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatus = string.Empty;
    [ObservableProperty] private bool _isReporting;
    [ObservableProperty] private string _reportStatus = string.Empty;

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

    [RelayCommand]
    private async Task ReportAsync()
    {
        if (IsReporting) return;

        var session = _sessionService.CurrentSession;
        if (session is null)
        {
            ReportStatus = "Sign in to report content.";
            return;
        }

        var dialog = new InputDialog("Report pack", $"Why are you reporting \"{Name}\"?") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText)) return;

        IsReporting = true;
        ReportStatus = string.Empty;
        try
        {
            var result = await _reportService.SubmitReportAsync(session, ContentReportKind.Pack, _pack.Id, Name, dialog.InputText).ConfigureAwait(true);
            ReportStatus = result.Success ? "Reported. Thanks for flagging it." : result.ErrorMessage ?? "Couldn't submit report.";
        }
        finally
        {
            IsReporting = false;
        }
    }
}
