using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Commands.Internal;
using Winche.Events.Session;

namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Extension methods for registering Winche.Events.Commands services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers command handlers and <see cref="ICommandDispatcher"/> with the DI container.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Delegate to register command handlers.</param>
    public static IServiceCollection AddWincheEventsCommands(
        this IServiceCollection services,
        Action<WincheEventsCommandsOptions> configure)
    {
        var options = new WincheEventsCommandsOptions();
        configure(options);

        foreach (var (commandType, aggregateType, handlerType) in options.Handlers)
        {
            var handlerInterfaceType = typeof(ICommandHandler<,>).MakeGenericType(commandType, aggregateType);
            services.AddSingleton(handlerInterfaceType, handlerType);
        }

        services.AddSingleton<ICommandDispatcher>(sp =>
        {
            var eventStore = sp.GetRequiredService<IEventStore>();
            return new CommandDispatcher(eventStore, type => sp.GetRequiredService(type));
        });

        return services;
    }
}
