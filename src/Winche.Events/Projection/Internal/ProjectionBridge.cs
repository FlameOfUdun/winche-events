using Marten.Events.Aggregation;
using Winche.Events.Abstractions;
using JasperFxEvent = JasperFx.Events.IEvent;

namespace Winche.Events.Projection.Internal;

internal sealed class ProjectionBridge<TAggregate>(Projection<TAggregate> projection)
    : SingleStreamProjection<TAggregate, string>
    where TAggregate : class, IAggregate
{
    private readonly Projection<TAggregate> _projection = projection;

    private static EventEnvelope<IEvent> ToEnvelope(string streamId, JasperFxEvent e)
        => new(e.Id.ToString(), streamId, (IEvent)e.Data, e.Version, e.Timestamp, e.Sequence, e.EventTypeName, e.DotNetTypeName);

    public override TAggregate? Evolve(TAggregate? snapshot, string id, JasperFxEvent @event)
    {
        var state = snapshot ?? _projection.Create(id);
        return _projection.ApplyEvent(state, ToEnvelope(id, @event));
    }
}
