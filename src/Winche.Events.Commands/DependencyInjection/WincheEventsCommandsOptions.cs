namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEventsCommands</c>.</summary>
public sealed class WincheEventsCommandsOptions
{
    internal readonly List<(Type CommandType, Type AggregateType, Type HandlerType)> Handlers = [];

    /// <summary>Registers a command handler.</summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <typeparam name="TAggregate">The aggregate type the command operates on.</typeparam>
    /// <typeparam name="THandler">The handler implementation.</typeparam>
    public void AddHandler<TCommand, TAggregate, THandler>()
        where TAggregate : class
        where THandler : class, ICommandHandler<TCommand, TAggregate>
        => Handlers.Add((typeof(TCommand), typeof(TAggregate), typeof(THandler)));
}
