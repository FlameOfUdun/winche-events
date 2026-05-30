using Winche.Events.Abstractions;

namespace Winche.Events.Projection;

/// <summary>
/// Base class for aggregate projections. Register handlers for each event type using
/// <c>On&lt;TEvent&gt;</c> in the constructor. Unregistered event types are silently ignored.
/// </summary>
/// <typeparam name="TAggregate">The aggregate state type produced by this projection.</typeparam>
public abstract class Projection<TAggregate> : ProjectionBase where TAggregate : class, IAggregate
{
    private readonly Dictionary<Type, Func<TAggregate, EventEnvelope<IEvent>, TAggregate>> _handlers = new();

    /// <summary>Returns the initial (empty) state for the given stream identifier.</summary>
    public abstract TAggregate Create(string id);

    /// <summary>Registers a handler for <typeparamref name="TEvent"/>.</summary>
    protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, TAggregate> handler)
        where TEvent : IEvent
        => _handlers[typeof(TEvent)] = (state, e) =>
            handler(state, new EventEnvelope<TEvent>(e.Id, e.StreamId, (TEvent)e.Data, e.Version, e.Timestamp, e.Sequence, e.TypeAlias, e.DotNetType));

    internal TAggregate ApplyEvent(TAggregate state, EventEnvelope<IEvent> envelope)
    {
        if (envelope.Data is null) return state;
        return _handlers.TryGetValue(envelope.Data.GetType(), out var handler)
            ? handler(state, envelope)
            : state;
    }
}
