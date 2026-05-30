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

    public async Task<DispatchResult> DispatchAsync<TAggregate>(
        string streamId,
        ICommand<TAggregate> command,
        CancellationToken ct = default)
        where TAggregate : class, IAggregate
    {
        await using var session = await _eventStore.OpenSessionAsync(ct: ct);
        var streamEnvelope   = await session.GetStreamAsync<TAggregate>(streamId, ct);
        var previousVersion  = streamEnvelope?.Version ?? 0;
        var handler          = _serviceProvider.GetRequiredService<CommandHandler<TAggregate>>();
        var events           = await handler.HandleAsync(streamEnvelope?.Aggregate, command, ct);
        await session.AppendStreamAsync(streamId, events, command.ExpectedVersion, ct);
        await session.SaveChangesAsync(ct);
        var newEvents = await session.GetEventsAsync(streamId, fromVersion: previousVersion + 1, ct);
        return new DispatchResult(newEvents, previousVersion + newEvents.Count);
    }
}
