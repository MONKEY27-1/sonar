using CommunityToolkit.Mvvm.ComponentModel;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One row in the Admin Panel's Reports grid — wraps a fetched
/// <see cref="ContentReportSummary"/> with per-row busy/status state for the Dismiss/Resolve
/// actions, same shape as <see cref="AdminCommunityPluginRowViewModel"/>.</summary>
public partial class AdminContentReportRowViewModel : ObservableObject
{
    public AdminContentReportRowViewModel(ContentReportSummary report)
    {
        Id = report.Id;
        ContentType = report.ContentType;
        ContentName = report.ContentName;
        ReporterUsername = report.ReporterUsername ?? "unknown";
        Reason = report.Reason;
        CreatedAt = report.CreatedAt;
    }

    public string Id { get; }
    public string ContentType { get; }
    public string ContentName { get; }
    public string ReporterUsername { get; }
    public string Reason { get; }
    public DateTime CreatedAt { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
}
