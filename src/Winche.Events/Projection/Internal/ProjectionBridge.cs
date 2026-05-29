using Marten.Events.Aggregation;
using JasperFx.Events;
using Winche.Events.Abstractions;
using JasperFxEvent = JasperFx.Events.IEvent;

namespace Winche.Events.Projection.Internal;

internal sealed class ProjectionBridge<TAggregate>(Projection<TAggregate> projection) : SingleStreamProjection<TAggregate, string>
    where TAggregate : class, IAggregate<string>
{
    private readonly Projection<TAggregate> _projection = projection;

    public override TAggregate? Evolve(TAggregate? snapshot, string id, JasperFxEvent @event)
    {
        var state = snapshot ?? _projection.Create(id);
        return _projection.ApplyEventAsync(state, @event.Data).GetAwaiter().GetResult();
    }
}
