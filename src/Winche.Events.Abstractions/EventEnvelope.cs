namespace Winche.Events.Abstractions;

/// <summary>
/// Wraps a domain event with the full metadata available from the event store row.
/// </summary>
/// <typeparam name="TEvent">The domain event type.</typeparam>
/// <param name="Id">Unique identifier assigned to this event by the store (sequential Guid as string).</param>
/// <param name="StreamId">The stream this event belongs to.</param>
/// <param name="Data">The typed domain event payload.</param>
/// <param name="Version">1-based position of this event within its stream.</param>
/// <param name="Timestamp">When the event was committed to the store (UTC).</param>
/// <param name="Sequence">Global sequential order of this event across the entire event store.</param>
/// <param name="TypeAlias">The stable type alias stored in the <c>type</c> column (e.g. <c>order_placed</c>).</param>
/// <param name="DotNetType">The .NET type name stored in the <c>dotnet_type</c> column (e.g. <c>MyApp.Domain.OrderPlaced, MyApp.Domain</c>).</param>
public sealed record EventEnvelope<TEvent>(
    string Id,
    string StreamId,
    TEvent Data,
    long Version,
    DateTimeOffset Timestamp,
    long Sequence,
    string TypeAlias,
    string DotNetType) where TEvent : IEvent;

/// <summary>Extension methods for working with collections of <see cref="EventEnvelope{TEvent}"/>.</summary>
public static class EventEnvelopeExtensions
{
    /// <summary>
    /// Filters a sequence to envelopes whose payload is <typeparamref name="TEvent"/> and returns
    /// them as strongly-typed <see cref="EventEnvelope{TEvent}"/> instances.
    /// </summary>
    public static IEnumerable<EventEnvelope<TEvent>> OfEventType<TEvent>(
        this IEnumerable<EventEnvelope<IEvent>> source)
        where TEvent : IEvent
        => source
            .Where(e => e.Data is TEvent)
            .Select(e => new EventEnvelope<TEvent>(e.Id, e.StreamId, (TEvent)e.Data, e.Version, e.Timestamp, e.Sequence, e.TypeAlias, e.DotNetType));
}
