namespace Winche.Events.Abstractions;

/// <summary>
/// Base interface for all aggregate types. Every aggregate must inherit from <c>Aggregate</c>.
/// </summary>
public interface IAggregate
{
    /// <summary>
    /// The stream identifier for this aggregate instance. Set by the projection infrastructure.
    /// </summary>
    string Id { get; init; }
}


/// <summary>
/// Base record for all aggregate state types. Most applications will use this type.
/// </summary>
public abstract record Aggregate : IAggregate
{
    /// <inheritdoc/>
    public string Id { get; init; } = Guid.NewGuid().ToString();
}
