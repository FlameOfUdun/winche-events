namespace Winche.Events.Projection;

/// <summary>Controls when and how an aggregate document is built from its event stream.</summary>
public enum ProjectionMode
{
    /// <summary>Aggregate is updated synchronously within the same transaction as the appended events. <see cref="Session.IEventSession.LoadAsync{TAggregate}"/> is a simple document lookup.</summary>
    Inline,

    /// <summary>Aggregate is updated by a background daemon after events are committed. Eventually consistent.</summary>
    Async,

    /// <summary>Aggregate is computed on every <see cref="Session.IEventSession.LoadAsync{TAggregate}"/> call by replaying the event stream. No stored document.</summary>
    Live,
}
