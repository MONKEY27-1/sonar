using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;

namespace Soundboard.ViewModels;

/// <summary>Backs the "Submit a Plugin" window (unlocked by installing Developer Tools) — lets a
/// user write a small script (run through the same sandbox as Community tab cards, see
/// <see cref="IPluginScriptRunner"/>), test it locally before publishing, then publish it to the
/// shared Community tab as an unverified submission — only an admin can mark it verified
/// afterward.</summary>
public partial class ScriptPluginAuthoringViewModel : ObservableObject
{
    private readonly ISessionService _sessionService;
    private readonly ICommunityPluginService _communityPluginService;
    private readonly IPluginScriptRunner _scriptRunner;
    private readonly IProfanityFilterService _profanityFilter;

    public ScriptPluginAuthoringViewModel(
        ISessionService sessionService,
        ICommunityPluginService communityPluginService,
        IPluginScriptRunner scriptRunner,
        IProfanityFilterService profanityFilter)
    {
        _sessionService = sessionService;
        _communityPluginService = communityPluginService;
        _scriptRunner = scriptRunner;
        _profanityFilter = profanityFilter;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty]
    private string _scriptSource =
        "// sonar.getSoundNames() -> list of your sound names\n" +
        "// sonar.playSound(name) -> play one by name\n" +
        "// sonar.log(message) -> show text in the output below\n\n" +
        "sonar.log(\"Hello from my plugin!\");\n";

    [ObservableProperty] private string _testRunOutput = string.Empty;
    [ObservableProperty] private bool _isTestRunning;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isPublishing;

    [RelayCommand]
    private async Task TestRunAsync()
    {
        if (IsTestRunning) return;

        IsTestRunning = true;
        TestRunOutput = string.Empty;
        try
        {
            var result = await _scriptRunner.RunAsync(ScriptSource).ConfigureAwait(true);
            var lines = new List<string>(result.LogLines);
            if (!result.Success)
            {
                lines.Add($"Error: {result.ErrorMessage}");
            }

            TestRunOutput = lines.Count > 0
                ? string.Join(Environment.NewLine, lines)
                : (result.Success ? "Ran successfully — no output." : "Failed.");
        }
        finally
        {
            IsTestRunning = false;
        }
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (IsPublishing) return;

        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Give your plugin a name first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ScriptSource))
        {
            StatusMessage = "Your plugin's script is empty.";
            return;
        }

        if (_profanityFilter.ContainsProfanity(Name) || _profanityFilter.ContainsProfanity(Description))
        {
            StatusMessage = "That name or description isn't allowed. Please choose something else.";
            return;
        }

        var session = _sessionService.CurrentSession;
        if (session is null)
        {
            StatusMessage = "Sign in first to publish a plugin.";
            return;
        }

        IsPublishing = true;
        StatusMessage = string.Empty;
        try
        {
            var result = await _communityPluginService
                .SubmitAsync(session, Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), ScriptSource)
                .ConfigureAwait(true);

            StatusMessage = result.Success
                ? "Published! It'll show as unverified until an admin reviews it."
                : result.ErrorMessage ?? "Couldn't publish.";
        }
        finally
        {
            IsPublishing = false;
        }
    }
}
