# Winche.Events

A [Marten](https://martendb.io/)-backed event sourcing library for .NET 10. Provides typed projections, an explicit unit-of-work session, optimistic concurrency, post-commit notifications, and an optional command-dispatch layer — all without exposing Marten types to your domain code.

---

## Packages

| Package | Purpose |
| - | - |
| `Winche.Events` | Core: event store, sessions, projections, notifiers |
| `Winche.Events.Commands` | Optional: command handlers and dispatcher |

---

## Getting started

### 1. Define your events

All events must inherit from `DomainEvent`:

```csharp
using Winche.Events.Models;

public record OrderPlaced(string OrderId, decimal Total) : DomainEvent;
public record OrderShipped(string OrderId) : DomainEvent;
public record OrderCancelled(string OrderId) : DomainEvent;
```

### 2. Define your aggregate and projection

```csharp
using Winche.Events.Projection;

public record Order(string Id, string Status, decimal Total);

public class OrderProjection : Projection<Order>
{
    public override Order InitialState() => new(string.Empty, "none", 0);

    public Order Apply(Order state, OrderPlaced e)    => state with { Id = e.OrderId, Status = "placed",  Total = e.Total };
    public Order Apply(Order state, OrderShipped e)   => state with { Status = "shipped" };
    public Order Apply(Order state, OrderCancelled e) => state with { Status = "cancelled" };
}
```

Each `Apply` overload handles one event type. Unhandled event types are ignored — no fallback method needed.

### 3. Register services

```csharp
using Winche.Events.DependencyInjection;
using Winche.Events.Projection;

services.AddWincheEvents(opts =>
{
    opts.ConnectionString = "Host=localhost;Database=mydb;Username=postgres;Password=...";

    opts.AddEventType<OrderPlaced>();
    opts.AddEventType<OrderShipped>();
    opts.AddEventType<OrderCancelled>();

    opts.AddProjection<OrderProjection, Order>(ProjectionMode.Live);
});
```

### 4. Use the event store

```csharp
var store = provider.GetRequiredService<IEventStore>();

await using var session = await store.OpenSessionAsync();

await session.AppendAsync<Order>("orders/123", [new OrderPlaced("orders/123", 49.99m)]);
await session.SaveChangesAsync();

var order = await session.LoadAsync<Order>("orders/123");
// order.Status == "placed"
```

---

## Projection modes

| Mode | Behaviour |
| - | - |
| `Live` | Aggregate is computed on every `LoadAsync` by replaying the event stream. No stored document. |
| `Inline` | Aggregate document is updated synchronously inside the same transaction when events are appended. `LoadAsync` is a simple document lookup. |
| `Async` | Aggregate document is updated by a background daemon. Eventually consistent. |

---

## IEventSession

`IEventSession` is a unit of work scoped to a single PostgreSQL connection and transaction. Always dispose it with `await using`.

```csharp
public interface IEventSession : IAsyncDisposable
{
    Task AppendAsync<TAggregate>(
        string streamId,
        IEnumerable<DomainEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class;

    Task SaveChangesAsync(CancellationToken ct = default);
}
```

**Optimistic concurrency** — pass `expectedVersion` to `AppendAsync` to reject concurrent writes:

```csharp
await session.AppendAsync<Order>("orders/123", events, expectedVersion: 3);
```

Marten throws if the stream's current version doesn't match.

---

## Post-commit notifications

Implement `IAppendNotifier` to receive a callback after each successful commit:

```csharp
using Winche.Events.Notification;

public class MyNotifier : IAppendNotifier
{
    public Task NotifyAsync(string streamId, string streamType,
        IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        // Runs after the PostgreSQL transaction commits.
        // Events are already persisted — this cannot roll them back.
        return Task.CompletedTask;
    }
}
```

Register it:

```csharp
opts.AddNotifier<MyNotifier>();
```

Multiple notifiers can be registered. Each runs independently; an exception in one is logged and swallowed and does not affect the others or the caller.

---

## Commands (Winche.Events.Commands)

The commands package adds a load-handle-append-return dispatch loop on top of `IEventSession`.

### 1. Define commands and handlers

```csharp
using Winche.Events.Commands;

public record PlaceOrderCommand(string OrderId, decimal Total);

public class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Order>
{
    public Task<IEnumerable<DomainEvent>> HandleAsync(
        PlaceOrderCommand cmd, Order? state, CancellationToken ct = default)
    {
        if (state is { Status: not "none" })
            throw new InvalidOperationException("Order already exists.");

        return Task.FromResult<IEnumerable<DomainEvent>>(
            [new OrderPlaced(cmd.OrderId, cmd.Total)]);
    }
}
```

The `state` argument is the current aggregate loaded from the store (`null` if the stream does not exist yet).

### 2. Register

```csharp
using Winche.Events.Commands.DependencyInjection;

services.AddWincheEventsCommands(commands =>
{
    commands.AddHandler<PlaceOrderCommand, Order, PlaceOrderHandler>();
});
```

### 3. Dispatch

```csharp
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

var order = await dispatcher.DispatchAsync<PlaceOrderCommand, Order>(
    "orders/123", new PlaceOrderCommand("orders/123", 49.99m));

// order reflects the state after the command's events have been applied
```

**Dispatch flow:**

1. Open a session
2. Load current aggregate state
3. Call handler → produce events
4. Append events and commit
5. Load and return updated state

---

## Transaction isolation

`OpenSessionAsync` accepts an optional `IsolationLevel`:

```csharp
await using var session = await store.OpenSessionAsync(IsolationLevel.Serializable);
```

Default is `ReadCommitted`.

---

## Requirements

- .NET 10
- PostgreSQL (via Marten / Npgsql)
