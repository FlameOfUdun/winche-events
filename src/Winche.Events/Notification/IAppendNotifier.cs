using Winche.Events.Models;

namespace Winche.Events.Notification;

/// <summary>
/// Receives a callback after each successful PostgreSQL commit.
/// Exceptions are logged and swallowed — they do not roll back the committed transaction.
/// </summary>
public interface IAppendNotifier
{
    /// <summary>Called once per <see cref="Session.IEventSession.SaveChangesAsync"/> for each stream that had events appended.</summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="streamType">The aggregate type name associated with the stream.</param>
    /// <param name="events">The events that were committed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyAsync(
        string streamId,
        string streamType,
        IReadOnlyList<DomainEvent> events,
        CancellationToken ct = default);
}
