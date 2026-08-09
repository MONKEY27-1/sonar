namespace Soundboard.Services;

/// <summary>Shared "run on a background thread, race against an outer timeout" guarantee used by
/// both <see cref="PluginScriptRunner"/> (one-shot script execution) and
/// <see cref="CommunityPluginRuntime"/> (dispatching a persisted tile/panel-button click). This
/// outer race is the actual guarantee the caller never hangs — Jint's own internal
/// statement/recursion/timeout limits are defense in depth on top of it, not a substitute for it,
/// since they depend on the script's own execution path actually reaching a checkpoint.</summary>
internal static class SandboxedExecution
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, T> work,
        Func<T> onTimeout,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var workTask = Task.Run(() => work(timeoutCts.Token), CancellationToken.None);

        var completed = await Task.WhenAny(workTask, Task.Delay(timeout + TimeSpan.FromSeconds(1), cancellationToken))
            .ConfigureAwait(false);

        if (completed != workTask)
        {
            return onTimeout();
        }

        return await workTask.ConfigureAwait(false);
    }
}
