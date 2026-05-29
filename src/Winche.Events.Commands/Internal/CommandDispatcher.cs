using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Session;

namespace Winche.Events.Commands.Internal;

internal sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IEventStore _eventStore;
    private readonly IServiceProvider _serviceProvider;

    internal CommandDispatcher(IEventStore eventStore, IServiceProvider serviceProvider)
    {
        _eventStore = eventStore;
        _serviceProvider = serviceProvider;
    }

    public async Task DispatchAsync<TAggregate>(
        string streamId,
        ICommand<TAggregate> command,
        long? expectedVersion = null,
        CancellationToken ct = default)
        where TAggregate : class, IAggregate
    {
        await using var session = await _eventStore.OpenSessionAsync(ct: ct);
        var state   = await session.LoadAsync<TAggregate>(streamId, ct);
        var handler = _serviceProvider.GetRequiredService<CommandHandler<TAggregate>>();
        var events  = await handler.HandleAsync(state, command, ct);
        await session.AppendAsync(streamId, events, expectedVersion, ct);
        await session.SaveChangesAsync(ct);
    }
}
