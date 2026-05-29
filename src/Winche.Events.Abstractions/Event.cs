namespace Winche.Events.Abstractions;

/// <summary>
/// Base interface for all domain events. Every event type must inherit from <c>DomainEvent</c>.
/// </summary>
public interface IEvent;

/// <summary>
/// Base record for all domain events.
/// </summary>
public abstract record Event : IEvent;
