using System;

namespace DalamudMCP.Events;

/// <summary>
/// Aggregates the event subsystem so it can be handed to tools as one object and
/// disposed with the plugin. The collector is created lazily on server start and
/// torn down on stop, so a stopped listener records nothing.
/// </summary>
public sealed class EventHub : IDisposable
{
    public EventBuffer Buffer { get; } = new();

    public EventCollector? Collector { get; private set; }

    public void Start(GameServices svc, EventCollectionConfig config)
    {
        Stop();
        Collector = new EventCollector(svc, Buffer, config);
    }

    public void Stop()
    {
        Collector?.Dispose();
        Collector = null;
    }

    public void Dispose() => Stop();
}
