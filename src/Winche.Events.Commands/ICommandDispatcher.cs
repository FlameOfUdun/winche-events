using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Dispatches commands through the load → handle → append → return cycle.
/// Register via <c>AddWincheEventsCommands</c> and resolve from the DI container.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Loads the current aggregate state, calls the registered handler, appends the produced events,
    /// commits, then returns the updated aggregate state. The command type is resolved at runtime
    /// from the registered handlers.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type the command operates on.</typeparam>
    /// <param name="streamId">The stream identifier for the aggregate instance.</param>
    /// <param name="command">The command to dispatch.</param>
    /// <param name="expectedVersion">If set, the commit fails if the stream version does not match (optimistic concurrency).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The aggregate state after the command's events have been applied, or <c>null</c> if the stream is empty.</returns>
    Task<TAggregate?> DispatchAsync<TAggregate>(
        string streamId,
        object command,
        long? expectedVersion = null,
        CancellationToken ct = default) where TAggregate : class, IAggregate;
}
