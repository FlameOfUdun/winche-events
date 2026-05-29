using Marten;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session.Internal;

namespace Winche.Events.DependencyInjection;

/// <summary>Extension methods for registering Winche.Events services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Winche.Events event store, projections, and notifiers with the DI container.</summary>
    public static IServiceCollection AddWincheEvents(
        this IServiceCollection services,
        Action<WincheEventsOptions> configure)
    {
        var options = new WincheEventsOptions();
        configure(options);

        foreach (var (projection, _) in options.Projections)
            projection.RegisterServices(services);

        foreach (var notifierType in options.NotifierTypes)
            services.AddSingleton(typeof(IAppendNotifier), notifierType);

        var inlineTypes = options.Projections
            .Where(p => p.Mode == ProjectionMode.Inline)
            .Select(p => p.Projection.AggregateType)
            .ToHashSet();

        services.AddMarten(sp =>
        {
            var storeOptions = new StoreOptions();
            storeOptions.Connection(options.ConnectionString);
            storeOptions.Events.StreamIdentity = StreamIdentity.AsString;

            foreach (var eventType in options.EventTypes)
                storeOptions.Events.AddEventType(eventType);

            foreach (var (projection, mode) in options.Projections)
                projection.ConfigureMarten(storeOptions, sp, ToMartenLifecycle(mode));

            return storeOptions;
        });

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
        ProjectionMode.Async => ProjectionLifecycle.Async,
        ProjectionMode.Live => ProjectionLifecycle.Live,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
