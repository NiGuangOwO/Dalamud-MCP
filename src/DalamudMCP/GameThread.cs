using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;

namespace DalamudMCP;

/// <summary>
/// Marshals work onto the game's framework thread.
///
/// Game memory (object table, player state, addons) is only guaranteed stable
/// while the client's own update loop is running, so every read goes through here.
/// A bounded timeout is essential: if the game is mid-zone or the framework thread
/// is wedged, we must return an error to the agent rather than hanging the HTTP
/// request forever.
/// </summary>
public sealed class GameThread : IGameThread, IDisposable
{
    private readonly IFramework framework;
    private readonly Action<string> log;
    private readonly TimeSpan timeout;

    public GameThread(IFramework framework, Action<string> log, TimeSpan timeout)
    {
        this.framework = framework;
        this.log = log;
        this.timeout = timeout;
    }

    public async Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (framework.IsFrameworkUnloading)
            throw new TargetInvocationCancelledException("The game client is shutting down; no game data is available.");

        var work = framework.RunOnFrameworkThread(func);
        var completed = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, work))
        {
            throw new TargetInvocationCancelledException(
                $"Timed out after {timeout.TotalSeconds:0.#}s waiting for the game's framework thread. " +
                "The client may be loading, zoned, or busy.");
        }

        return await work.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _ = log;
    }
}
