using Winche.Events.Abstractions;

namespace Winche.Events.Session;

/// <summary>
/// A unit of work scoped to a single PostgreSQL connection and transaction.
/// Call <see cref="SaveChangesAsync"/> to commit, then dispose with <c>await using</c>.
/// </summary>
public interface IEventSession : IAsyncDisposable
{
    /// <summary>
    /// Buffers <paramref name="events"/> to be appended to <paramref name="streamId"/> on the next <see cref="SaveChangesAsync"/>.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="events">Events to append.</param>
    /// <param name="expectedVersion">If set, the commit will fail if the stream's current version does not match (optimistic concurrency).</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendAsync(
        string streamId,
        IEnumerable<IEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the current aggregate state for <paramref name="streamId"/> by applying its projection.
    /// Returns <c>null</c> if the stream does not exist.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type to load.</typeparam>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class, IAggregate<string>;

    /// <summary>
    /// Commits all buffered events to PostgreSQL, then fires registered <see cref="Notification.IAppendNotifier"/> instances.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);
}
