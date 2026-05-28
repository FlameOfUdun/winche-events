using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Models;
using Winche.Events.Notification;
using Winche.Events.Projection;

namespace Winche.Events.Session.Internal;

internal sealed class EventSession : IEventSession
{
    private readonly IDocumentSession _session;
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlySet<Type> _inlineProjectionTypes;
    private readonly IReadOnlyList<IAppendNotifier> _notifiers;
    private readonly ILogger<EventSession> _logger;

    private readonly List<(string StreamId, string StreamType, List<DomainEvent> Events)> _pending = [];

    internal EventSession(
        IDocumentSession session,
        IServiceProvider serviceProvider,
        IReadOnlySet<Type> inlineProjectionTypes,
        IReadOnlyList<IAppendNotifier> notifiers,
        ILogger<EventSession> logger)
    {
        _session = session;
        _serviceProvider = serviceProvider;
        _inlineProjectionTypes = inlineProjectionTypes;
        _notifiers = notifiers;
        _logger = logger;
    }

    public Task AppendAsync<TAggregate>(
        string streamId,
        IEnumerable<DomainEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        var eventList = events.ToList();

        if (expectedVersion.HasValue)
            _session.Events.Append(streamId, expectedVersion.Value, eventList.ToArray());
        else
            _session.Events.Append(streamId, eventList.ToArray());

        _pending.Add((streamId, typeof(TAggregate).Name, eventList));
        return Task.CompletedTask;
    }

    public async Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class
    {
        if (_inlineProjectionTypes.Contains(typeof(TAggregate)))
            return await _session.LoadAsync<TAggregate>(streamId, ct);

        var projection = _serviceProvider.GetRequiredService<Projection<TAggregate>>();
        var rawEvents = await _session.Events.FetchStreamAsync(streamId, token: ct);
        if (rawEvents.Count == 0) return null;

        var state = projection.InitialState();
        foreach (var e in rawEvents)
            state = projection.ApplyEvent(state, e.Data);

        return state;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _session.SaveChangesAsync(ct);
        await FireNotifiersAsync(ct);
        _pending.Clear();
    }

    private async Task FireNotifiersAsync(CancellationToken ct)
    {
        foreach (var (streamId, streamType, events) in _pending)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(streamId, streamType, events, ct);
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
