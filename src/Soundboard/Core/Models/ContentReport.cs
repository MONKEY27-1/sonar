namespace Soundboard.Core.Models;

/// <summary>Which kind of Marketplace content a report is about — mirrors the
/// content_reports.content_type check constraint ('plugin'/'pack') in supabase-schema.sql.</summary>
public enum ContentReportKind
{
    Plugin,
    Pack
}

public static class ContentReportKindExtensions
{
    public static string ToWireValue(this ContentReportKind kind) => kind switch
    {
        ContentReportKind.Plugin => "plugin",
        ContentReportKind.Pack => "pack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
