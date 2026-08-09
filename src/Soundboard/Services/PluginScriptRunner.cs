using Jint;
using Jint.Runtime;
using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

/// <summary>Executes a Community Plugin script inside Jint, a sandboxed JavaScript interpreter —
/// deliberately never given CLR/.NET access (no `.AllowClr()` call anywhere in this class), so a
/// script cannot reference any .NET type at all. The only capability a script has is whatever
/// <see cref="PluginScriptHost"/> exposes. A fresh <see cref="Engine"/> is created per run — never
/// reused or shared across plugins or calls, so nothing from one run can leak into another.
///
/// Two independent layers guarantee this never hangs the caller: Jint's own built-in
/// statement/recursion/timeout limits, AND an outer <see cref="Task.Run(Action)"/> + timeout race
/// in this class that returns a timeout result regardless of whether Jint's internal enforcement
/// behaves as expected for a given script — the outer layer is the actual guarantee, the inner
/// one is defense in depth.</summary>
public sealed class PluginScriptRunner : IPluginScriptRunner
{
    internal static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(2);

    private readonly ILibraryService _libraryService;
    private readonly IPlaybackManager _playbackManager;

    public PluginScriptRunner(ILibraryService libraryService, IPlaybackManager playbackManager)
    {
        _libraryService = libraryService;
        _playbackManager = playbackManager;
    }

    public Task<PluginScriptResult> RunAsync(string scriptSource, CancellationToken cancellationToken = default)
    {
        var logLines = new List<string>();

        return SandboxedExecution.RunAsync(
            ct => Execute(scriptSource, logLines, ct),
            () => new PluginScriptResult { Success = false, ErrorMessage = "Script timed out.", LogLines = logLines },
            ExecutionTimeout,
            cancellationToken);
    }

    private PluginScriptResult Execute(string scriptSource, List<string> logLines, CancellationToken cancellationToken)
    {
        try
        {
            var engine = new Engine(options =>
            {
                options.LimitRecursion(64);
                options.MaxStatements(10_000);
                options.TimeoutInterval(ExecutionTimeout);
                options.CancellationToken(cancellationToken);
                // Deliberately no options.AllowClr() — this is the actual sandbox boundary.
                // Without it, the script has zero ability to reference any .NET type; the only
                // capability it has is whatever object is explicitly registered below.
            });

            // Test Run is ephemeral and one-shot — tile/panel-button registrations aren't tracked
            // here (that only matters for an installed plugin, see CommunityPluginRuntime), so
            // these lists are thrown away once Execute returns.
            var host = new PluginScriptHost(_libraryService, _playbackManager, logLines, [], []);
            engine.SetValue("sonar", host);

            engine.Execute(scriptSource);

            return new PluginScriptResult { Success = true, LogLines = logLines };
        }
        catch (JavaScriptException ex)
        {
            return new PluginScriptResult { Success = false, ErrorMessage = $"Script error: {ex.Message}", LogLines = logLines };
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return new PluginScriptResult { Success = false, ErrorMessage = "Script timed out.", LogLines = logLines };
        }
        catch (Exception ex)
        {
            // Broad by design — statement/recursion-overflow exceptions and any other Jint
            // failure mode all collapse to "the script failed," which is all a plugin author
            // needs from Test Run. Nothing here is silently swallowed; it's always reported.
            return new PluginScriptResult { Success = false, ErrorMessage = $"Script failed: {ex.Message}", LogLines = logLines };
        }
    }
}
