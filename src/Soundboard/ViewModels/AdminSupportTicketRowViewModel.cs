using CommunityToolkit.Mvvm.ComponentModel;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One entry in the Admin Panel's Support ticket list — wraps a fetched
/// <see cref="SupportTicket"/>. Status is mutable (bound to the reply panel's dropdown once
/// selected) and IsSelected drives the list's highlight, same shape as
/// <see cref="SupportTicketRowViewModel"/> on the user side.</summary>
public partial class AdminSupportTicketRowViewModel : ObservableObject
{
    public AdminSupportTicketRowViewModel(SupportTicket ticket)
    {
        Id = ticket.Id;
        Username = ticket.Username ?? "unknown";
        Subject = ticket.Subject;
        CreatedAt = ticket.CreatedAt;
        _status = ticket.Status;
    }

    public string Id { get; }
    public string Username { get; }
    public string Subject { get; }
    public DateTime CreatedAt { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string _status;

    [ObservableProperty] private bool _isSelected;

    public string StatusLabel => Status switch
    {
        "in_progress" => "In Progress",
        "resolved" => "Resolved",
        _ => "Open"
    };
}
