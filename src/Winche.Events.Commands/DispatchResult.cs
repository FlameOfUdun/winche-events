using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Returned by <see cref="ICommandDispatcher.DispatchAsync{TAggregate}"/> after a successful commit.
/// Contains the events the server appended and the new stream version.
/// </summary>
public sealed record DispatchResult(
    IReadOnlyList<EventEnvelope<IEvent>> Events,
    long Version);
