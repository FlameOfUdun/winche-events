using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEventsCommands</c>.</summary>
public sealed class WincheEventsCommandsOptions
{
    internal readonly List<Action<IServiceCollection>> Registrations = [];

    /// <summary>
    /// Registers a command handler. All commands for <typeparamref name="TAggregate"/> are handled
    /// by a single <typeparamref name="THandler"/> class using <c>On&lt;TCommand&gt;</c> registrations.
    /// </summary>
    /// <typeparam name="THandler">The handler class inheriting <c>CommandHandler&lt;TAggregate&gt;</c>.</typeparam>
    /// <typeparam name="TAggregate">The aggregate type this handler operates on.</typeparam>
    public void AddCommandHandler<THandler, TAggregate>()
        where THandler : CommandHandler<TAggregate>
        where TAggregate : class, IAggregate
        => Registrations.Add(services => services.AddSingleton<CommandHandler<TAggregate>, THandler>());
}
