using Jint;
using Jint.Runtime;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

/// <summary>Owns installed Community Plugins — see <see cref="ICommunityPluginRuntime"/> for the
/// contract. One live <see cref="Engine"/> per installed plugin, kept alive for the app's session;
/// never shared, never reused across plugins. Still fully sandboxed (no <c>AllowClr()</c> call
/// anywhere in this class, same as <see cref="PluginScriptRunner"/>) — persistence and UI
/// integration are the only things new here, not the security boundary.</summary>
public sealed class CommunityPluginRuntime : ICommunityPluginRuntime
{
    private readonly ILibraryService _libraryService;
    private readonly IPlaybackManager _playbackManager;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notifications;
    private readonly Dictionary<string, InstalledPluginState> _installed = new();

    public CommunityPluginRuntime(
        ILibraryService libraryService,
        IPlaybackManager playbackManager,
        ISettingsService settingsService,
        INotificationService notifications)
    {
        _libraryService = libraryService;
        _playbackManager = playbackManager;
        _settingsService = settingsService;
        _notifications = notifications;
    }

    public IReadOnlyList<PluginTile> Tiles { get; private set; } = [];
    public IReadOnlyList<PluginPanelButtonGroup> PanelGroups { get; private set; } = [];

    public event EventHandler? PluginsChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot the list before iterating — LoadPlugin never mutates settings, but this keeps
        // the loop's source stable regardless.
        var cached = _settingsService.Settings.Plugins.InstalledCommunityPlugins.ToList();

        foreach (var entry in cached)
        {
            try
            {
                LoadPlugin(entry.Id, entry.Name, entry.ScriptSource);
            }
            catch (Exception ex)
            {
                // One bad cached script (edited by hand, or genuinely buggy) must never block the
                // rest of the installed plugins — or startup itself — from loading.
                _notifications.ShowError("Plugin failed to load", $"\"{entry.Name}\": {ex.Message}");
            }
        }

        RebuildPublicState();
        return Task.CompletedTask;
    }

    public async Task<bool> InstallAsync(CommunityPlugin plugin, CancellationToken cancellationToken = default)
    {
        if (IsInstalled(plugin.Id)) return true;

        try
        {
            LoadPlugin(plugin.Id, plugin.Name, plugin.ScriptSource);
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't install plugin", ex.Message);
            return false;
        }

        var settings = _settingsService.Settings;
        settings.Plugins.InstalledCommunityPlugins.Add(new InstalledCommunityPlugin
        {
            Id = plugin.Id,
            Name = plugin.Name,
            ScriptSource = plugin.ScriptSource
        });
        await _settingsService.SaveAsync(cancellationToken: cancellationToken).ConfigureAwait(true);

        RebuildPublicState();
        return true;
    }

    public void Uninstall(string pluginId)
    {
        if (!_installed.Remove(pluginId)) return;

        var settings = _settingsService.Settings;
        settings.Plugins.InstalledCommunityPlugins.RemoveAll(p => p.Id == pluginId);
        _ = _settingsService.SaveAsync();

        RebuildPublicState();
    }

    public bool IsInstalled(string pluginId) => _installed.ContainsKey(pluginId);

    private void LoadPlugin(string id, string name, string scriptSource)
    {
        var engine = new Engine(options =>
        {
            options.LimitRecursion(64);
            options.MaxStatements(10_000);
            options.TimeoutInterval(PluginScriptRunner.ExecutionTimeout);
            // Deliberately no options.AllowClr() — same sandbox boundary as PluginScriptRunner.
        });

        var logLines = new List<string>();
        var tileRegs = new List<PluginTileRegistration>();
        var buttonRegs = new List<PluginButtonRegistration>();
        var host = new PluginScriptHost(_libraryService, _playbackManager, logLines, tileRegs, buttonRegs);
        engine.SetValue("sonar", host);

        engine.Execute(scriptSource);

        _installed[id] = new InstalledPluginState(engine, name, logLines, tileRegs, buttonRegs);
    }

    /// <summary>Dispatches a stored callback through the exact same background-thread + outer-
    /// timeout race as a one-shot script run (see SandboxedExecution), plus two things specific to
    /// a persisted, repeatedly-invoked engine: engine.Constraints.Reset() gives this specific call
    /// a fresh statement/timeout budget (Jint's constraints otherwise drain across the engine's
    /// whole lifetime — confirmed empirically before this was built), and a per-plugin semaphore
    /// prevents two overlapping clicks on the same plugin from entering its Engine concurrently
    /// (Jint's Engine isn't safe for concurrent use, and rapid double-clicks are a real scenario a
    /// one-shot Test Run never had to account for).
    ///
    /// Returns a PluginScriptResult (same shape a Test Run reports) rather than just succeeding
    /// silently or throwing — a click has nowhere in the main window to show sonar.log(...) output
    /// or a script error unless the caller (PluginTileViewModel/PluginPanelButtonViewModel)
    /// explicitly surfaces this result via INotificationService.</summary>
    private static async Task<PluginScriptResult> InvokeCallbackAsync(InstalledPluginState state, Action callback)
    {
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var startLogCount = state.LogLines.Count;

            var result = await SandboxedExecution.RunAsync(
                _ => ExecuteCallback(state.Engine, callback),
                () => new PluginScriptResult { Success = false, ErrorMessage = "Timed out." },
                PluginScriptRunner.ExecutionTimeout,
                CancellationToken.None).ConfigureAwait(false);

            // Only this call's own new log lines — state.LogLines accumulates across the
            // plugin's whole installed lifetime (capped at 200 total, see PluginScriptHost.Log).
            var newLogLines = state.LogLines.Skip(startLogCount).ToList();

            return new PluginScriptResult
            {
                Success = result.Success,
                ErrorMessage = result.ErrorMessage,
                LogLines = newLogLines
            };
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static PluginScriptResult ExecuteCallback(Engine engine, Action callback)
    {
        try
        {
            engine.Constraints.Reset();
            callback();
            return new PluginScriptResult { Success = true };
        }
        catch (JavaScriptException ex)
        {
            return new PluginScriptResult { Success = false, ErrorMessage = $"Script error: {ex.Message}" };
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return new PluginScriptResult { Success = false, ErrorMessage = "Timed out." };
        }
        catch (Exception ex)
        {
            // Broad by design, same reasoning as PluginScriptRunner.Execute — statement/recursion
            // overflow and any other Jint failure mode all collapse to one reported failure.
            return new PluginScriptResult { Success = false, ErrorMessage = $"Failed: {ex.Message}" };
        }
    }

    private void RebuildPublicState()
    {
        var tiles = new List<PluginTile>();
        var groups = new List<PluginPanelButtonGroup>();

        foreach (var (id, state) in _installed)
        {
            foreach (var tile in state.Tiles)
            {
                var capturedTile = tile;
                tiles.Add(new PluginTile
                {
                    PluginId = id,
                    Name = capturedTile.Name,
                    Icon = capturedTile.Icon,
                    InvokeAsync = () => InvokeCallbackAsync(state, capturedTile.OnClick)
                });
            }

            if (state.PanelButtons.Count > 0)
            {
                groups.Add(new PluginPanelButtonGroup
                {
                    PluginName = state.Name,
                    Buttons = state.PanelButtons.Select(button =>
                    {
                        var capturedButton = button;
                        return new PluginPanelButton
                        {
                            Label = capturedButton.Label,
                            InvokeAsync = () => InvokeCallbackAsync(state, capturedButton.OnClick)
                        };
                    }).ToList()
                });
            }
        }

        Tiles = tiles;
        PanelGroups = groups;
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class InstalledPluginState
    {
        public InstalledPluginState(Engine engine, string name, List<string> logLines, List<PluginTileRegistration> tiles, List<PluginButtonRegistration> panelButtons)
        {
            Engine = engine;
            Name = name;
            LogLines = logLines;
            Tiles = tiles;
            PanelButtons = panelButtons;
        }

        public Engine Engine { get; }
        public string Name { get; }
        public List<string> LogLines { get; }
        public List<PluginTileRegistration> Tiles { get; }
        public List<PluginButtonRegistration> PanelButtons { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
