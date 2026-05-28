using Marten;
using Marten.Events;
using Marten.Events.Projections;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Projection.Internal;
using Winche.Events.Session;
using Winche.Events.Session.Internal;

namespace Winche.Events.DependencyInjection;

/// <summary>Extension methods for registering Winche.Events services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Winche.Events event store, projections, and notifiers with the DI container.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Delegate to configure the event store options.</param>
    public static IServiceCollection AddWincheEvents(
        this IServiceCollection services,
        Action<WincheEventsOptions> configure)
    {
        var options = new WincheEventsOptions();
        configure(options);

        // Register each Projection<TAggregate> as a singleton so EventSession can resolve it for live aggregation
        foreach (var reg in options.Projections)
        {
            var projectionBaseType = typeof(Projection<>).MakeGenericType(reg.AggregateType);
            services.AddSingleton(projectionBaseType, reg.ProjectionType);
        }

        // Register notifiers
        foreach (var notifierType in options.NotifierTypes)
            services.AddSingleton(typeof(IAppendNotifier), notifierType);

        // Track which aggregate types have inline projections for fast-path LoadAsync
        var inlineTypes = options.Projections
            .Where(r => r.Mode == ProjectionMode.Inline)
            .Select(r => r.AggregateType)
            .ToHashSet();

        // Configure Marten using the service-provider factory overload so projections can be resolved
        // Note (Marten 9.x): StreamIdentity is in JasperFx.Events; ProjectionLifecycle is in JasperFx.Events.Projections
        services.AddMarten(sp =>
        {
            var storeOptions = new StoreOptions();
            storeOptions.Connection(options.ConnectionString);
            storeOptions.Events.StreamIdentity = StreamIdentity.AsString;

            foreach (var eventType in options.EventTypes)
                storeOptions.Events.AddEventType(eventType);

            foreach (var reg in options.Projections)
            {
                var projectionBaseType = typeof(Projection<>).MakeGenericType(reg.AggregateType);
                var projection = sp.GetRequiredService(projectionBaseType);

                var bridgeType = typeof(ProjectionBridge<>).MakeGenericType(reg.AggregateType);
                var bridge = Activator.CreateInstance(bridgeType, projection)!;

                // AddGlobalProjection<TAggregate, TId>(SingleStreamProjection<TAggregate, TId>, ProjectionLifecycle)
                var addMethod = typeof(ProjectionOptions)
                    .GetMethod(nameof(ProjectionOptions.AddGlobalProjection))!
                    .MakeGenericMethod(reg.AggregateType, typeof(string));
                addMethod.Invoke(storeOptions.Projections, [bridge, ToMartenLifecycle(reg.Mode)]);
            }

            return storeOptions;
        });

        // Register IEventStore as a singleton resolved lazily from the built service provider
        services.AddSingleton<Session.IEventStore>(sp =>
        {
            var martenStore = sp.GetRequiredService<IDocumentStore>();
            var notifiers = sp.GetServices<IAppendNotifier>().ToList();
            var logger = sp.GetRequiredService<ILogger<EventSession>>();
            return new EventStore(martenStore, sp, inlineTypes, notifiers, logger);
        });

        return services;
    }

    private static ProjectionLifecycle ToMartenLifecycle(ProjectionMode mode) => mode switch
    {
        ProjectionMode.Inline => ProjectionLifecycle.Inline,
        ProjectionMode.Async  => ProjectionLifecycle.Async,
        ProjectionMode.Live   => ProjectionLifecycle.Live,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
