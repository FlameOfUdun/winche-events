using Winche.Events.Abstractions;

namespace Winche.Events.Projection;

/// <summary>
/// Base class for aggregate projections. Register handlers for each event type using
/// <c>On&lt;TEvent&gt;</c> in the constructor. Unregistered event types are silently ignored.
/// </summary>
/// <typeparam name="TAggregate">The aggregate state type produced by this projection.</typeparam>
public abstract class Projection<TAggregate> : ProjectionBase where TAggregate : class, IAggregate
{
    private readonly Dictionary<Type, Func<TAggregate, EventEnvelope<IEvent>, TAggregate>> _syncHandlers = new();
    private readonly Dictionary<Type, Func<TAggregate, EventEnvelope<IEvent>, Task<TAggregate>>> _asyncHandlers = new();

    /// <summary>Returns the initial (empty) state for the given stream identifier.</summary>
    public abstract TAggregate Create(string id);

    /// <summary>Registers a synchronous handler for <typeparamref name="TEvent"/>.</summary>
    protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, TAggregate> handler)
        where TEvent : IEvent
        => _syncHandlers[typeof(TEvent)] = (state, e) =>
            handler(state, new EventEnvelope<TEvent>(e.StreamId, (TEvent)e.Data, e.Version, e.Timestamp));

    /// <summary>Registers an asynchronous handler for <typeparamref name="TEvent"/>.</summary>
    protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, Task<TAggregate>> handler)
        where TEvent : IEvent
        => _asyncHandlers[typeof(TEvent)] = (state, e) =>
            handler(state, new EventEnvelope<TEvent>(e.StreamId, (TEvent)e.Data, e.Version, e.Timestamp));

    internal TAggregate ApplyEvent(TAggregate state, EventEnvelope<IEvent> envelope)
    {
        if (envelope.Data is null) return state;
        return _syncHandlers.TryGetValue(envelope.Data.GetType(), out var handler)
            ? handler(state, envelope)
            : state;
    }

    internal async Task<TAggregate> ApplyEventAsync(TAggregate state, EventEnvelope<IEvent> envelope)
    {
        if (envelope.Data is null) return state;
        var type = envelope.Data.GetType();
        if (_asyncHandlers.TryGetValue(type, out var asyncHandler))
            return await asyncHandler(state, envelope);
        if (_syncHandlers.TryGetValue(type, out var syncHandler))
            return syncHandler(state, envelope);
        return state;
    }
}
