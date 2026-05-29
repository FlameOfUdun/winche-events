using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Base class for aggregate command handlers. Register handlers for each command type using
/// <c>On&lt;TCommand&gt;</c> in the constructor. All commands for one aggregate live in one class.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this handler operates on.</typeparam>
public abstract class CommandHandler<TAggregate> where TAggregate : class, IAggregate
{
    private readonly Dictionary<Type, Func<TAggregate?, object, CancellationToken, Task<IEnumerable<IEvent>>>> _handlers = new();

    /// <summary>Registers a synchronous command handler.</summary>
    protected void On<TCommand>(Func<TAggregate?, TCommand, IEnumerable<IEvent>> handler)
        where TCommand : ICommand<TAggregate>
        => _handlers[typeof(TCommand)] = (state, cmd, _) =>
            Task.FromResult(handler(state, (TCommand)cmd));

    /// <summary>Registers an asynchronous command handler.</summary>
    protected void On<TCommand>(Func<TAggregate?, TCommand, Task<IEnumerable<IEvent>>> handler)
        where TCommand : ICommand<TAggregate>
        => _handlers[typeof(TCommand)] = (state, cmd, _) =>
            handler(state, (TCommand)cmd);

    /// <summary>Registers an asynchronous command handler with cancellation support.</summary>
    protected void On<TCommand>(Func<TAggregate?, TCommand, CancellationToken, Task<IEnumerable<IEvent>>> handler)
        where TCommand : ICommand<TAggregate>
        => _handlers[typeof(TCommand)] = (state, cmd, ct) =>
            handler(state, (TCommand)cmd, ct);

    internal Task<IEnumerable<IEvent>> HandleAsync(TAggregate? state, object command, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(command.GetType(), out var handler))
            throw new InvalidOperationException(
                $"No handler registered for '{command.GetType().Name}' on aggregate '{typeof(TAggregate).Name}'.");
        return handler(state, command, ct);
    }
}
