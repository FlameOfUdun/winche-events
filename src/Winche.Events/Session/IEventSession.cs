using Winche.Events.Abstractions;

namespace Winche.Events.Session;

/// <summary>
/// A unit of work scoped to a single PostgreSQL connection and transaction.
/// Call <see cref="SaveChangesAsync"/> to commit, then dispose with <c>await using</c>.
/// </summary>
public interface IEventSession : IAsyncDisposable
{
    /// <summary>
    /// Buffers <paramref name="events"/> to be appended to <paramref name="streamId"/> on the next
    /// <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task AppendAsync(
        string streamId,
        IEnumerable<IEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the stored aggregate document for <paramref name="streamId"/>.
    /// Returns <c>null</c> if the stream does not exist or no document has been stored yet.
    /// For Inline projections the document is always current after <see cref="SaveChangesAsync"/>.
    /// For Async projections the document is eventually consistent.
    /// </summary>
    Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class, IAggregate;

    /// <summary>
    /// Waits for all running async projection daemons to catch up to the latest committed event,
    /// then loads the stored aggregate document. Use this after <see cref="IEventStore.OpenSessionAsync"/>
    /// when you need guaranteed fresh state from an <see cref="Projection.ProjectionMode.Async"/> projection.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="timeout">How long to wait for the daemon. Defaults to 5 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TAggregate?> LoadFreshAsync<TAggregate>(
        string streamId,
        TimeSpan timeout = default,
        CancellationToken ct = default) where TAggregate : class, IAggregate;

    /// <summary>
    /// Returns all events for <paramref name="streamId"/> in order, each wrapped with stream metadata.
    /// Returns an empty list if the stream does not exist.
    /// </summary>
    Task<IReadOnlyList<EventEnvelope<IEvent>>> GetEventsAsync(
        string streamId,
        CancellationToken ct = default);

    /// <summary>
    /// Commits all buffered events to PostgreSQL, then fires registered notifiers.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
