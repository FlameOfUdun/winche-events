using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Projection;

namespace Winche.Events.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEvents</c>.</summary>
public sealed class WincheEventsOptions
{
    /// <summary>PostgreSQL connection string passed to Marten.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    internal readonly List<Type> EventTypes = [];
    internal readonly List<(ProjectionBase Projection, ProjectionMode Mode)> Projections = [];
    internal readonly List<Type> NotifierTypes = [];

    /// <summary>Registers an event type.</summary>
    /// <typeparam name="TEvent">The event type to register.</typeparam>
    public void AddEvent<TEvent>() where TEvent : class, IEvent
    {
        EventTypes.Add(typeof(TEvent));
    }

    /// <summary>
    /// Registers a projection with the specified lifecycle mode.
    /// </summary>
    /// <typeparam name="TProjection">The projection class, which must inherit from <c>Projection&lt;TAggregate&gt;</c>.</typeparam>
    /// <param name="mode">Controls when the aggregate document is built from events.</param>
    public void AddProjection<TProjection>(ProjectionMode mode) where TProjection : ProjectionBase, new()
    {
        Projections.Add((new TProjection(), mode));
    }

    /// <summary>Registers a post-commit notifier. Multiple notifiers can be registered.</summary>
    public void AddNotifier<TNotifier>() where TNotifier : class, IAppendNotifier
    {
        NotifierTypes.Add(typeof(TNotifier));
    }
}
