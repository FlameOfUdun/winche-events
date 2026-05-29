using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Commands.Internal;
using Winche.Events.Session;

namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Extension methods for registering Winche.Events.Commands services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers command handlers and <see cref="ICommandDispatcher"/> with the DI container.</summary>
    public static IServiceCollection AddWincheEventsCommands(
        this IServiceCollection services,
        Action<WincheEventsCommandsOptions> configure)
    {
        var options = new WincheEventsCommandsOptions();
        configure(options);

        foreach (var reg in options.Registrations)
            reg(services);

        services.AddSingleton<ICommandDispatcher>(sp =>
            new CommandDispatcher(sp.GetRequiredService<IEventStore>(), sp));

        return services;
    }
}
