using Marten.Events.Aggregation;
using JasperFx.Events;
using System.Reflection;

namespace Winche.Events.Projection.Internal;

internal sealed class ProjectionBridge<TAggregate>(Projection<TAggregate> projection) : SingleStreamProjection<TAggregate, string>
    where TAggregate : class
{
    private static readonly PropertyInfo? IdProperty = typeof(TAggregate).GetProperty("Id", typeof(string));

    private readonly Projection<TAggregate> _projection = projection;

    public override TAggregate? Evolve(TAggregate? snapshot, string id, IEvent @event)
    {
        var state = snapshot ?? CreateInitialState(id);
        return _projection.ApplyEvent(state, @event.Data);
    }

    private TAggregate CreateInitialState(string streamId)
    {
        var state = _projection.InitialState();
        IdProperty?.SetValue(state, streamId);
        return state;
    }
}
