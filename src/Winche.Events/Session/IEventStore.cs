using System.Data;

namespace Winche.Events.Session;

/// <summary>
/// Factory for <see cref="IEventSession"/> instances. Register via
/// <c>AddWincheEvents</c> and resolve from the DI container.
/// </summary>
public interface IEventStore
{
    /// <summary>Opens a new unit-of-work session backed by a PostgreSQL connection and transaction.</summary>
    /// <param name="isolationLevel">Transaction isolation level. Defaults to <see cref="IsolationLevel.ReadCommitted"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEventSession> OpenSessionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default);
}
