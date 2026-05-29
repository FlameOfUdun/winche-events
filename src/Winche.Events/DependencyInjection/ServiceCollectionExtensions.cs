using Marten;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;
using Winche.Events.Session;
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

        foreach (var proj in options.Projections)
            proj.Register(services);

        foreach (var notifierType in options.NotifierTypes)
            services.AddSingleton(typeof(IAppendNotifier), notifierType);

        services.AddMarten(sp =>
        {
            var storeOptions = new StoreOptions();
            storeOptions.Connection(options.ConnectionString);
            storeOptions.Events.StreamIdentity = StreamIdentity.AsString;

            foreach (var (eventType, alias) in options.EventTypes)
            {
                storeOptions.Events.AddEventType(eventType);
                if (alias is not null)
                    storeOptions.Events.MapEventType(eventType, alias);
            }

            if (options.ConfigureJsonSerializer is not null)
                storeOptions.UseSystemTextJsonForSerialization(configure: options.ConfigureJsonSerializer);

            foreach (var proj in options.Projections)
                proj.Configure(storeOptions, sp);

            return storeOptions;
        });

        services.AddSingleton<Session.IEventStore>(sp =>
        {
            var martenStore = sp.GetRequiredService<IDocumentStore>();
            var notifiers = sp.GetServices<IAppendNotifier>().ToList();
            var logger = sp.GetRequiredService<ILogger<EventSession>>();
            return new EventStore(martenStore, notifiers, logger);
        });

        return services;
    }
}
