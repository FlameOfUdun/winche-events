using Winche.Events.Session;

namespace Winche.Events.Commands.Internal;

internal sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IEventStore _eventStore;
    private readonly Func<Type, object> _handlerResolver;

    internal CommandDispatcher(IEventStore eventStore, Func<Type, object> handlerResolver)
    {
        _eventStore = eventStore;
        _handlerResolver = handlerResolver;
    }

    public async Task<TAggregate?> DispatchAsync<TCommand, TAggregate>(
        string streamId,
        TCommand command,
        long? expectedVersion = null,
        CancellationToken ct = default) where TAggregate : class
    {
        await using var session = await _eventStore.OpenSessionAsync(ct: ct);

        var currentState = await session.LoadAsync<TAggregate>(streamId, ct);

        var handlerType = typeof(ICommandHandler<TCommand, TAggregate>);
        var handler = (ICommandHandler<TCommand, TAggregate>)_handlerResolver(handlerType);

        var events = await handler.HandleAsync(command, currentState, ct);

        await session.AppendAsync<TAggregate>(streamId, events, expectedVersion, ct);
        await session.SaveChangesAsync(ct);

        return await session.LoadAsync<TAggregate>(streamId, ct);
    }
}
