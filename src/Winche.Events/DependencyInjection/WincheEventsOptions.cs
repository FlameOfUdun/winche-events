using System.Text.Json;
using JasperFx.Events.Projections;
using Marten;
using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Marten.Events.Aggregation;
using Winche.Events.Projection.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Events.DependencyInjection;

internal sealed record ProjectionRegistration(
    Action<IServiceCollection> Register,
    Action<StoreOptions, IServiceProvider> Configure);

/// <summary>Configuration options for <c>AddWincheEvents</c>.</summary>
public sealed class WincheEventsOptions
{
    /// <summary>PostgreSQL connection string passed to Marten.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    internal readonly List<(Type EventType, string? Alias)> EventTypes = [];
    internal readonly List<ProjectionRegistration> Projections = [];
    internal readonly List<Type> NotifierTypes = [];

    /// <summary>
    /// Configures the <see cref="JsonSerializerOptions"/> used by Marten for event serialization.
    /// When <see langword="null"/> Marten's default camelCase settings apply.
    /// </summary>
    public Action<JsonSerializerOptions>? ConfigureJsonSerializer { get; set; }

    /// <summary>Registers an event type. Marten derives the stored alias from the class name.</summary>
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
    /// Registers a projection. Both type parameters and the projection mode are explicit —
    /// no runtime type discovery occurs.
    /// </summary>
    /// <typeparam name="TProjection">Concrete projection class inheriting <c>Projection&lt;TAggregate&gt;</c>.</typeparam>
    /// <typeparam name="TAggregate">The aggregate type produced by this projection.</typeparam>
    /// <param name="mode">
    /// <see cref="ProjectionMode.Inline"/>: updated in the same transaction as the append.<br/>
    /// <see cref="ProjectionMode.Async"/>: updated by the background daemon after commit.
    /// Projection handlers are always synchronous — async enrichment belongs in command handlers.
    /// </param>
    public void AddProjection<TProjection, TAggregate>(ProjectionMode mode)
        where TProjection : Projection<TAggregate>
        where TAggregate : class, IAggregate
    {
        var lifecycle = mode == ProjectionMode.Inline
            ? ProjectionLifecycle.Inline
            : ProjectionLifecycle.Async;

        Projections.Add(new ProjectionRegistration(
            Register: services => services.AddSingleton<Projection<TAggregate>, TProjection>(),
            Configure: (opts, sp) =>
            {
                var projection = sp.GetRequiredService<Projection<TAggregate>>();
                var bridge = new ProjectionBridge<TAggregate>(projection);
                opts.Projections.AddGlobalProjection<TAggregate, string>(bridge, lifecycle);
            }
        ));
    }

    /// <summary>Registers a post-commit notifier. Multiple notifiers can be registered.</summary>
    public void AddNotifier<TNotifier>() where TNotifier : class, IAppendNotifier
        => NotifierTypes.Add(typeof(TNotifier));
}
