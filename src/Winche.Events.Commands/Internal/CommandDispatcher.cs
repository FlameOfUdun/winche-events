using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Winche.Events.Abstractions;
using Winche.Events.Session;

namespace Winche.Events.Commands.Internal;

internal sealed class CommandDispatcher : ICommandDispatcher
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> _handleMethodCache = new();

    private readonly IEventStore _eventStore;
    private readonly Func<Type, object> _handlerResolver;

    internal CommandDispatcher(IEventStore eventStore, Func<Type, object> handlerResolver)
    {
        _eventStore = eventStore;
        _handlerResolver = handlerResolver;
    }

    public async Task<TAggregate?> DispatchAsync<TAggregate>(
        string streamId,
        object command,
        long? expectedVersion = null,
        CancellationToken ct = default) where TAggregate : class, IAggregate<string>
    {
        await using var session = await _eventStore.OpenSessionAsync(ct: ct);

        var currentState = await session.LoadAsync<TAggregate>(streamId, ct);

        var handlerInterfaceType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TAggregate));
        var handler = _handlerResolver(handlerInterfaceType);
        var handleMethod = _handleMethodCache.GetOrAdd(handlerInterfaceType, t => t.GetMethod("HandleAsync")!);
        Task<IEnumerable<IEvent>> handleTask;
        try
        {
            handleTask = (Task<IEnumerable<IEvent>>)handleMethod.Invoke(handler, [command, currentState, ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable — satisfies the compiler
        }
        var events = await handleTask;

        await session.AppendAsync(streamId, events, expectedVersion, ct);
        await session.SaveChangesAsync(ct);

        return await session.LoadAsync<TAggregate>(streamId, ct);
    }
}
