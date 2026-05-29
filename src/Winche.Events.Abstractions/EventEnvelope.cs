namespace Winche.Events.Abstractions;

/// <summary>
/// Wraps a domain event with the stream metadata available at the point of processing.
/// </summary>
/// <typeparam name="TEvent">The domain event type.</typeparam>
/// <param name="StreamId">The stream the event belongs to.</param>
/// <param name="Data">The typed domain event payload.</param>
/// <param name="Version">1-based position of this event within its stream.</param>
/// <param name="Timestamp">When the event was committed to the store.</param>
public sealed record EventEnvelope<TEvent>(
    string StreamId,
    TEvent Data,
    long Version,
    DateTimeOffset Timestamp) where TEvent : IEvent;
