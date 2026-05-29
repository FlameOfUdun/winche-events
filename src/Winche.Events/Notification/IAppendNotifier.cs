using Winche.Events.Abstractions;

namespace Winche.Events.Notification;

/// <summary>
/// Receives a callback after each successful PostgreSQL commit.
/// Exceptions are logged and swallowed — they do not roll back the committed transaction.
/// </summary>
public interface IAppendNotifier
{
    /// <summary>Called once per <see cref="Session.IEventSession.SaveChangesAsync"/> for each stream that had events appended.</summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="events">The events that were committed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyAsync(
        string streamId,
        IReadOnlyList<IEvent> events,
        CancellationToken ct = default);
}
