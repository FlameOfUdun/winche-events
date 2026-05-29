namespace Winche.Events.Projection;

/// <summary>Controls when and how the aggregate document is built from its event stream.</summary>
public enum ProjectionMode
{
    /// <summary>
    /// Updated synchronously within the same transaction as the appended events.
    /// <see cref="Session.IEventSession.GetStateAsync{TAggregate}"/> returns the fresh document immediately.
    /// Handlers registered with <c>On&lt;TEvent&gt;</c> must not perform external I/O — they run inside
    /// the open PostgreSQL transaction.
    /// </summary>
    Inline,

    /// <summary>
    /// Updated by a background daemon after events are committed. Eventually consistent.
    /// Safe for async handlers that perform external DB reads or API calls.
    /// </summary>
    Async,
}
