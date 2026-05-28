using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Handles a command against an aggregate, returning the domain events to append.
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TAggregate">The aggregate type the command operates on.</typeparam>
public interface ICommandHandler<TCommand, TAggregate> where TAggregate : class
{
    /// <summary>
    /// Validates the command against <paramref name="currentState"/> and returns the events to persist.
    /// Throw to reject the command — no events will be appended.
    /// </summary>
    /// <param name="command">The incoming command.</param>
    /// <param name="currentState">Current aggregate state, or <c>null</c> if the stream does not exist yet.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<DomainEvent>> HandleAsync(
        TCommand command,
        TAggregate? currentState,
        CancellationToken ct = default);
}
