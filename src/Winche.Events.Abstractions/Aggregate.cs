namespace Winche.Events.Abstractions;

/// <summary>
/// Base interface for all aggregate types. Every aggregate must inherit from <c>Aggregate</c>.
/// </summary>
/// <typeparam name="TKey">The type of the aggregate's identifier.</typeparam>
public interface IAggregate<TKey>
{
    /// <summary>
    /// The stream identifier for this aggregate instance. Set by the projection infrastructure.
    /// </summary>
    TKey Id { get; init; }
}

/// <summary>
/// Base record for all aggregate state types.
/// </summary>
public abstract record Aggregate<TKey> : IAggregate<TKey>
{
    /// <inheritdoc />
    public TKey Id { get; init; } = default!;
}

/// <summary>
/// Base record for all aggregate state types with string identifiers. Most applications will use this type.
/// </summary>
public abstract record Aggregate : Aggregate<string>;
