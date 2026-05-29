using Marten.Events.Aggregation;
using Winche.Events.Abstractions;
using JasperFxEvent = JasperFx.Events.IEvent;

namespace Winche.Events.Projection.Internal;

// JasperFx requires exactly one of Evolve/EvolveAsync to be overridden per bridge instance.
// Inline projections use the sync bridge; Async projections use the async bridge.

internal sealed class InlineProjectionBridge<TAggregate>(Projection<TAggregate> projection)
    : SingleStreamProjection<TAggregate, string>
    where TAggregate : class, IAggregate
{
    private readonly Projection<TAggregate> _projection = projection;

    private static EventEnvelope<IEvent> ToEnvelope(string streamId, JasperFxEvent e)
        => new(streamId, (IEvent)e.Data, e.Version, e.Timestamp);

    public override TAggregate? Evolve(TAggregate? snapshot, string id, JasperFxEvent @event)
    {
        var state = snapshot ?? _projection.Create(id);
        return _projection.ApplyEvent(state, ToEnvelope(id, @event));
    }
}

internal sealed class AsyncProjectionBridge<TAggregate>(Projection<TAggregate> projection)
    : SingleStreamProjection<TAggregate, string>
    where TAggregate : class, IAggregate
{
    private readonly Projection<TAggregate> _projection = projection;

    private static EventEnvelope<IEvent> ToEnvelope(string streamId, JasperFxEvent e)
        => new(streamId, (IEvent)e.Data, e.Version, e.Timestamp);

    public override async ValueTask<TAggregate?> EvolveAsync(
        TAggregate? snapshot, string id,
        Marten.IQuerySession session,
        JasperFxEvent @event,
        CancellationToken cancellation)
    {
        var state = snapshot ?? _projection.Create(id);
        return await _projection.ApplyEventAsync(state, ToEnvelope(id, @event));
    }
}
