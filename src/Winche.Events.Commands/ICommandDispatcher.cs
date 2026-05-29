using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Dispatches commands through the load → handle → append → commit cycle.
/// Register via <c>AddWincheEventsCommands</c> and resolve from the DI container.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Loads the current aggregate state, calls the registered <see cref="CommandHandler{TAggregate}"/>,
    /// appends the produced events, and commits. Does not return state — call
    /// <see cref="Session.IEventSession.LoadAsync{TAggregate}"/> explicitly if you need state after dispatch.
    /// <typeparamref name="TAggregate"/> is inferred from the command's <c>ICommand&lt;TAggregate&gt;</c>
    /// implementation — no explicit type argument needed at the call site.
    /// </summary>
    Task DispatchAsync<TAggregate>(
        string streamId,
        ICommand<TAggregate> command,
        long? expectedVersion = null,
        CancellationToken ct = default)
        where TAggregate : class, IAggregate;
}
