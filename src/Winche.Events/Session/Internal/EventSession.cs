using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging;
using Winche.Events.Abstractions;
using Winche.Events.Notification;

namespace Winche.Events.Session.Internal;

internal sealed class EventSession : IEventSession
{
    private readonly IDocumentSession _session;
    private readonly IDocumentStore _store;
    private readonly IReadOnlyList<IAppendNotifier> _notifiers;
    private readonly ILogger<EventSession> _logger;
    private readonly List<(string StreamId, List<IEvent> Events)> _pending = [];

    internal EventSession(
        IDocumentSession session,
        IDocumentStore store,
        IReadOnlyList<IAppendNotifier> notifiers,
        ILogger<EventSession> logger)
    {
        _session = session;
        _store = store;
        _notifiers = notifiers;
        _logger = logger;
    }

    public Task AppendAsync(
        string streamId,
        IEnumerable<IEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        var eventList = events.ToList();
        if (expectedVersion.HasValue)
            _session.Events.Append(streamId, expectedVersion.Value, eventList.ToArray());
        else
            _session.Events.Append(streamId, eventList.ToArray());
        _pending.Add((streamId, eventList));
        return Task.CompletedTask;
    }

    public Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class, IAggregate
        => _session.LoadAsync<TAggregate>(streamId, ct);

    public async Task<TAggregate?> LoadFreshAsync<TAggregate>(
        string streamId,
        TimeSpan timeout = default,
        CancellationToken ct = default) where TAggregate : class, IAggregate
    {
        var effectiveTimeout = timeout == default ? TimeSpan.FromSeconds(5) : timeout;
        await _store.WaitForNonStaleProjectionDataAsync(effectiveTimeout);
        return await _session.LoadAsync<TAggregate>(streamId, ct);
    }

    public async Task<IReadOnlyList<EventEnvelope<IEvent>>> GetEventsAsync(
        string streamId,
        CancellationToken ct = default)
    {
        var raw = await _session.Events.FetchStreamAsync(streamId, token: ct);
        return [..raw.Select(e => new EventEnvelope<IEvent>(streamId, (IEvent)e.Data, e.Version, e.Timestamp))];
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _session.SaveChangesAsync(ct);
        await FireNotifiersAsync(ct);
        _pending.Clear();
    }

    private async Task FireNotifiersAsync(CancellationToken ct)
    {
        foreach (var (streamId, events) in _pending)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(streamId, events, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notifier {Type} failed for stream {StreamId}",
                        notifier.GetType().Name, streamId);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
