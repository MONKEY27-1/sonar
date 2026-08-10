using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>Backs the Support window — a master-detail chat: the ticket list on the left,
/// the selected ticket's message thread (or a new-ticket composer) on the right. Everything is
/// scoped to the signed-in user via RLS on support_tickets/support_ticket_messages. Only ever
/// shown while logged in, same as AccountViewModel.</summary>
public partial class SupportViewModel : ObservableObject
{
    private readonly ISessionService _sessionService;
    private readonly ISupportTicketService _supportTicketService;

    public SupportViewModel(ISessionService sessionService, ISupportTicketService supportTicketService)
    {
        _sessionService = sessionService;
        _supportTicketService = supportTicketService;
    }

    public ObservableCollection<SupportTicketRowViewModel> Tickets { get; } = [];
    public ObservableCollection<SupportMessageViewModel> Messages { get; } = [];

    [ObservableProperty] private SupportTicketRowViewModel? _selectedTicket;
    [ObservableProperty] private bool _isComposingNewTicket;
    [ObservableProperty] private string _newTicketSubject = string.Empty;
    [ObservableProperty] private string _newTicketMessage = string.Empty;
    [ObservableProperty] private string _replyText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoadingMessages;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool ShowEmptyState => SelectedTicket is null && !IsComposingNewTicket;

    partial void OnSelectedTicketChanged(SupportTicketRowViewModel? value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        _ = LoadMessagesAsync();
    }

    partial void OnIsComposingNewTicketChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    [RelayCommand]
    private async Task LoadAsync()
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var tickets = await _supportTicketService.GetMyTicketsAsync(session).ConfigureAwait(true);

        Tickets.Clear();
        foreach (var ticket in tickets)
        {
            Tickets.Add(new SupportTicketRowViewModel(ticket));
        }
    }

    [RelayCommand]
    private void SelectTicket(SupportTicketRowViewModel? ticket)
    {
        IsComposingNewTicket = false;
        ReplyText = string.Empty;
        StatusMessage = string.Empty;
        SelectedTicket = ticket;

        foreach (var row in Tickets)
        {
            row.IsSelected = ReferenceEquals(row, ticket);
        }
    }

    [RelayCommand]
    private void StartNewTicket()
    {
        SelectedTicket = null;
        IsComposingNewTicket = true;
        NewTicketSubject = string.Empty;
        NewTicketMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    private async Task LoadMessagesAsync()
    {
        Messages.Clear();
        if (SelectedTicket is null) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        IsLoadingMessages = true;
        try
        {
            var messages = await _supportTicketService.GetMessagesAsync(session, SelectedTicket.Id).ConfigureAwait(true);
            foreach (var message in messages)
            {
                Messages.Add(new SupportMessageViewModel(message));
            }
        }
        finally
        {
            IsLoadingMessages = false;
        }
    }

    [RelayCommand]
    private async Task CreateTicketAsync()
    {
        if (IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        if (string.IsNullOrWhiteSpace(NewTicketSubject) || string.IsNullOrWhiteSpace(NewTicketMessage))
        {
            StatusMessage = "Enter a subject and a message.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var result = await _supportTicketService.CreateTicketAsync(session, NewTicketSubject, NewTicketMessage).ConfigureAwait(true);
            if (!result.Success || result.Value is null)
            {
                StatusMessage = result.ErrorMessage ?? "Couldn't submit your request.";
                return;
            }

            IsComposingNewTicket = false;
            await LoadAsync().ConfigureAwait(true);
            SelectedTicket = Tickets.FirstOrDefault(t => t.Id == result.Value);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendReplyAsync()
    {
        if (IsBusy || SelectedTicket is null) return;
        if (string.IsNullOrWhiteSpace(ReplyText)) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var result = await _supportTicketService.SendMessageAsync(session, SelectedTicket.Id, ReplyText).ConfigureAwait(true);
            if (!result.Success)
            {
                StatusMessage = result.ErrorMessage ?? "Couldn't send your message.";
                return;
            }

            ReplyText = string.Empty;
            await LoadMessagesAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One entry in the Support window's ticket list — wraps a fetched
/// <see cref="SupportTicket"/> with a display-friendly status label plus IsSelected for the
/// list's highlight, toggled by <see cref="SupportViewModel.SelectTicketCommand"/>.</summary>
public sealed partial class SupportTicketRowViewModel : ObservableObject
{
    public SupportTicketRowViewModel(SupportTicket ticket)
    {
        Id = ticket.Id;
        Subject = ticket.Subject;
        Status = ticket.Status;
        StatusLabel = ticket.Status switch
        {
            "in_progress" => "In Progress",
            "resolved" => "Resolved",
            _ => "Open"
        };
        CreatedAt = ticket.CreatedAt;
    }

    public string Id { get; }
    public string Subject { get; }
    public string Status { get; }
    public string StatusLabel { get; }
    public DateTime CreatedAt { get; }

    /// <summary>A resolved ticket is closed to the user — the reply box hides in favor of a
    /// prompt to start a new request. Enforced server-side too, see send_ticket_message() in
    /// supabase-schema.sql.</summary>
    public bool IsResolved => Status == "resolved";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>One chat bubble — wraps a fetched <see cref="SupportTicketMessage"/>. SenderUsername
/// is always the real sender; each window's XAML decides how to label "your own" messages
/// (e.g. "You") since that depends on who's viewing, not on the message itself.</summary>
public sealed class SupportMessageViewModel
{
    public SupportMessageViewModel(SupportTicketMessage message)
    {
        Body = message.Body;
        IsAdmin = message.IsAdmin;
        SenderUsername = message.SenderUsername ?? "unknown";
        CreatedAt = message.CreatedAt;
    }

    public string Body { get; }
    public bool IsAdmin { get; }
    public string SenderUsername { get; }
    public DateTime CreatedAt { get; }
}
