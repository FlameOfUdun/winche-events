namespace Winche.Events.Abstractions;

/// <summary>
/// Snapshot of a Marten stream row (<c>mt_streams</c>) combined with the projected aggregate document.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type managed by this stream.</typeparam>
/// <param name="Id">The stream identifier.</param>
/// <param name="Aggregate">The current projected aggregate document, or <c>null</c> if none exists yet.</param>
/// <param name="Version">The total number of events appended to the stream.</param>
/// <param name="Created">When the stream was first created (UTC).</param>
/// <param name="LastModified">When the stream last received an event (UTC).</param>
/// <param name="IsArchived">Whether the stream has been archived.</param>
/// <param name="AggregateType">The .NET aggregate type alias stored in the <c>type</c> column.</param>
public sealed record StreamEnvelope<TAggregate>(
    string Id,
    TAggregate? Aggregate,
    long Version,
    DateTimeOffset Created,
    DateTimeOffset LastModified,
    bool IsArchived,
    string AggregateType) where TAggregate : class, IAggregate;
