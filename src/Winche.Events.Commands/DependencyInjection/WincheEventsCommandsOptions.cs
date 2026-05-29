namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEventsCommands</c>.</summary>
public sealed class WincheEventsCommandsOptions
{
    internal readonly List<(Type CommandType, Type AggregateType, Type HandlerType)> Handlers = [];

    /// <summary>
    /// Registers a command handler. The command and aggregate types are inferred from the
    /// <c>ICommandHandler&lt;TCommand, TAggregate&gt;</c> interface the handler implements.
    /// </summary>
    /// <typeparam name="THandler">The handler implementation, which must implement <c>ICommandHandler&lt;TCommand, TAggregate&gt;</c>.</typeparam>
    public void AddHandler<THandler>()
        where THandler : class
    {
        var handlerType = typeof(THandler);
        var iface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
            ?? throw new InvalidOperationException($"{handlerType.Name} must implement ICommandHandler<TCommand, TAggregate>.");

        var args = iface.GetGenericArguments();
        Handlers.Add((args[0], args[1], handlerType));
    }
}
