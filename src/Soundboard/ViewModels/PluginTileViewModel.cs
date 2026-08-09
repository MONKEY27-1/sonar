using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;

namespace Soundboard.ViewModels;

/// <summary>One tile in the main window's plugin tile strip — thin wrapper around a
/// <see cref="PluginTile"/> so its <c>InvokeAsync</c> delegate can be bound as a command.
/// Surfaces the click's result (any sonar.log(...) lines, or an error) via
/// <see cref="INotificationService"/> — the status bar is the only place in the main window a
/// click's output can show up, there's no dedicated per-tile output area.</summary>
public sealed partial class PluginTileViewModel : ObservableObject
{
    private readonly PluginTile _tile;
    private readonly INotificationService _notifications;

    public PluginTileViewModel(PluginTile tile, INotificationService notifications)
    {
        _tile = tile;
        _notifications = notifications;
    }

    public string Name => _tile.Name;
    public string Icon => _tile.Icon;

    [RelayCommand]
    private async Task InvokeAsync()
    {
        var result = await _tile.InvokeAsync().ConfigureAwait(true);
        PluginResultNotifier.Notify(_notifications, Name, result);
    }
}

/// <summary>One button inside the consolidated "🧩 Plugins" panel — same wrapping idea as
/// <see cref="PluginTileViewModel"/>, for a <see cref="PluginPanelButton"/>.</summary>
public sealed partial class PluginPanelButtonViewModel : ObservableObject
{
    private readonly PluginPanelButton _button;
    private readonly INotificationService _notifications;

    public PluginPanelButtonViewModel(PluginPanelButton button, INotificationService notifications)
    {
        _button = button;
        _notifications = notifications;
    }

    public string Label => _button.Label;

    [RelayCommand]
    private async Task InvokeAsync()
    {
        var result = await _button.InvokeAsync().ConfigureAwait(true);
        PluginResultNotifier.Notify(_notifications, Label, result);
    }
}

/// <summary>One installed plugin's section within the consolidated Plugins panel — a name header
/// plus its buttons.</summary>
public sealed class PluginPanelGroupViewModel
{
    public required string PluginName { get; init; }
    public required IReadOnlyList<PluginPanelButtonViewModel> Buttons { get; init; }
}

/// <summary>Shared "how a plugin click's result becomes visible" logic for both tiles and panel
/// buttons — an error always shows; log output only shows if there was any, so a tile that just
/// plays a sound (no sonar.log calls) stays silent rather than popping a redundant notification
/// on every click.</summary>
internal static class PluginResultNotifier
{
    public static void Notify(INotificationService notifications, string title, PluginScriptResult result)
    {
        if (!result.Success)
        {
            notifications.ShowError(title, result.ErrorMessage ?? "Something went wrong.");
        }
        else if (result.LogLines.Count > 0)
        {
            notifications.ShowInfo(title, string.Join(" | ", result.LogLines));
        }
    }
}
