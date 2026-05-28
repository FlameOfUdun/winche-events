using Winche.Events.Notification;
using Winche.Events.Projection;

namespace Winche.Events.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEvents</c>.</summary>
public sealed class WincheEventsOptions
{
    /// <summary>PostgreSQL connection string passed to Marten.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    internal readonly List<Type> EventTypes = [];
    internal readonly List<ProjectionRegistration> Projections = [];
    internal readonly List<Type> NotifierTypes = [];

    /// <summary>Registers an event type so Marten can serialize and deserialize it.</summary>
    public void AddEventType<TEvent>()
    {
        EventTypes.Add(typeof(TEvent));
    }

    /// <summary>Registers a projection and its aggregate type with the specified lifecycle mode.</summary>
    /// <typeparam name="TProjection">The projection class.</typeparam>
    /// <typeparam name="TAggregate">The aggregate state type produced by the projection.</typeparam>
    /// <param name="mode">Controls when the aggregate document is built from events.</param>
    public void AddProjection<TProjection, TAggregate>(ProjectionMode mode)
        where TProjection : Projection<TAggregate>
        where TAggregate : class
    {
        Projections.Add(new ProjectionRegistration(typeof(TProjection), typeof(TAggregate), mode));
    }

    /// <summary>Registers a post-commit notifier. Multiple notifiers can be registered.</summary>
    public void AddNotifier<TNotifier>() where TNotifier : class, IAppendNotifier
    {
        NotifierTypes.Add(typeof(TNotifier));
    }
}

internal sealed record ProjectionRegistration(Type ProjectionType, Type AggregateType, ProjectionMode Mode);
