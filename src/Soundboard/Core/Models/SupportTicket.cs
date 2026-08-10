namespace Soundboard.Core.Models;

/// <summary>A support conversation's metadata — shown both to the submitting user (their own
/// tickets only, enforced by RLS) and to admins (every ticket, via
/// admin_list_support_tickets()). The actual conversation is a separate list of
/// <see cref="SupportTicketMessage"/>, fetched per-ticket. Username is only populated for the
/// admin view; a user reading their own tickets already knows who they are.</summary>
public sealed class SupportTicket
{
    public required string Id { get; init; }
    public string? Username { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>One message in a support ticket's thread, from either the submitting user or an
/// admin. SenderUsername is the sender's own username regardless of which side sent it — the
/// UI decides how to label "your own" messages (e.g. "You") based on which window is showing
/// it, since that's viewer-dependent, not a fact about the message itself.</summary>
public sealed class SupportTicketMessage
{
    public required string Id { get; init; }
    public required string TicketId { get; init; }
    public string? SenderUsername { get; init; }
    public bool IsAdmin { get; init; }
    public required string Body { get; init; }
    public DateTime CreatedAt { get; init; }
}
