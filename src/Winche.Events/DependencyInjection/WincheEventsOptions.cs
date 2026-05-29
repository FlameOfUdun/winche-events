using System.Text.Json;
using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Projection;

namespace Winche.Events.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEvents</c>.</summary>
public sealed class WincheEventsOptions
{
    /// <summary>PostgreSQL connection string passed to Marten.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    internal readonly List<(Type EventType, string? Alias)> EventTypes = [];
    internal readonly List<(ProjectionBase Projection, ProjectionMode Mode)> Projections = [];
    internal readonly List<Type> NotifierTypes = [];

    /// <summary>
    /// Configures the <see cref="JsonSerializerOptions"/> used by Marten
    /// for event serialization.  Use this to register custom converters or change the
    /// naming policy.  When <see langword="null"/> Marten's default camelCase settings apply.
    /// </summary>
    public Action<JsonSerializerOptions>? ConfigureJsonSerializer { get; set; }

    /// <summary>
    /// Registers an event type.  Marten derives the stored type alias from the class name
    /// (e.g. <c>OrderPlaced</c> → <c>order_placed</c>).
    /// </summary>
    public void AddEvent<TEvent>() where TEvent : class, IEvent
        => EventTypes.Add((typeof(TEvent), null));

    /// <summary>
    /// Registers an event type with an explicit, stable type alias.
    /// The alias is written to Marten's <c>type</c> column and must never change once events
    /// have been persisted — renaming the C# class is safe as long as the alias stays the same.
    /// </summary>
    public void AddEvent<TEvent>(string alias) where TEvent : class, IEvent
        => EventTypes.Add((typeof(TEvent), alias));

    /// <summary>
    /// Registers a projection with the specified lifecycle mode.
    /// </summary>
    /// <typeparam name="TProjection">The projection class, which must inherit from <c>Projection&lt;TAggregate&gt;</c>.</typeparam>
    /// <param name="mode">Controls when the aggregate document is built from events.</param>
    public void AddProjection<TProjection>(ProjectionMode mode) where TProjection : ProjectionBase, new()
        => Projections.Add((new TProjection(), mode));

    /// <summary>Registers a post-commit notifier. Multiple notifiers can be registered.</summary>
    public void AddNotifier<TNotifier>() where TNotifier : class, IAppendNotifier
        => NotifierTypes.Add(typeof(TNotifier));
}
