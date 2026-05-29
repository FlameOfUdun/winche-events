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

### 1. Define your domain model

Aggregates inherit from `Aggregate` (or implement `IAggregate<string>` directly for multiple-inheritance scenarios). Events inherit from `Event` (or implement `IEvent`).

```csharp
using Winche.Events.Abstractions;

public record Order(string Status, decimal Total) : Aggregate
{
    public static Order Empty => new("none", 0);
}

public record OrderPlaced(string OrderId, decimal Total) : Event;
public record OrderShipped(string OrderId) : Event;
public record OrderCancelled(string OrderId) : Event;
```

### 2. Define your projection

```csharp
using Winche.Events.Projection;

public class OrderProjection : Projection<Order>
{
    public override Order Create(string id) => Order.Empty with { Id = id };

    public Order Apply(Order state, OrderPlaced e)    => state with { Status = "placed",    Total = e.Total };
    public Order Apply(Order state, OrderShipped e)   => state with { Status = "shipped" };
    public Order Apply(Order state, OrderCancelled e) => state with { Status = "cancelled" };
}
```

Each `Apply` overload handles one event type. Unhandled event types are silently ignored. Async work is supported via `ApplyAsync` overloads — see [Async projections](#async-projections).

### 3. Register services

```csharp
using Winche.Events.DependencyInjection;
using Winche.Events.Projection;

services.AddWincheEvents(opts =>
{
    opts.ConnectionString = "Host=localhost;Database=mydb;Username=postgres;Password=...";

    opts.AddEvent<OrderPlaced>();
    opts.AddEvent<OrderShipped>();
    opts.AddEvent<OrderCancelled>();

    opts.AddProjection<OrderProjection>(ProjectionMode.Live);
});
```

`AddProjection` infers the aggregate type from the `Projection<TAggregate>` base class. `OrderProjection` must have a parameterless constructor.

### 4. Use the event store

```csharp
var store = provider.GetRequiredService<IEventStore>();

await using var session = await store.OpenSessionAsync();

await session.AppendAsync("orders/123", [new OrderPlaced("orders/123", 49.99m)]);
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
    Task AppendAsync(
        string streamId,
        IEnumerable<IEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class, IAggregate<string>;

    Task SaveChangesAsync(CancellationToken ct = default);
}
```

**Optimistic concurrency** — pass `expectedVersion` to `AppendAsync` to reject concurrent writes:

```csharp
await session.AppendAsync("orders/123", events, expectedVersion: 3);
```

Marten throws if the stream's current version does not match.

---

## Async projections

`Apply` overloads can be replaced with `ApplyAsync` for projections that need to perform async work (database lookups, external calls, etc.):

```csharp
public class OrderProjection : Projection<Order>
{
    public override Order Create(string id) => Order.Empty with { Id = id };

    public async Task<Order> ApplyAsync(Order state, OrderPlaced e)
    {
        var enriched = await _db.GetOrderMetaAsync(e.OrderId);
        return state with { Status = "placed", Total = e.Total, Meta = enriched };
    }

    public Order Apply(Order state, OrderShipped e) => state with { Status = "shipped" };
}
```

`ApplyAsync` takes priority over `Apply` for the same event type when both are defined. Methods are resolved once per event type and cached for the lifetime of the application.

---

## Post-commit notifications

Implement `IAppendNotifier` to receive a callback after each successful commit:

```csharp
using Winche.Events.Notification;

public class MyNotifier : IAppendNotifier
{
    public Task NotifyAsync(string streamId, IReadOnlyList<IEvent> events,
        CancellationToken ct = default)
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

Multiple notifiers can be registered. Each runs independently — an exception in one is logged and swallowed and does not affect the others or the caller.

---

## Commands (Winche.Events.Commands)

The commands package adds a load → handle → append → return dispatch loop on top of `IEventSession`.

### 1. Define commands and handlers

```csharp
using Winche.Events.Commands;

public record PlaceOrderCommand(string OrderId, decimal Total);

public class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Order>
{
    public Task<IEnumerable<IEvent>> HandleAsync(
        PlaceOrderCommand cmd, Order? state, CancellationToken ct = default)
    {
        if (state is { Status: not "none" })
            throw new InvalidOperationException("Order already exists.");

        return Task.FromResult<IEnumerable<IEvent>>(
            [new OrderPlaced(cmd.OrderId, cmd.Total)]);
    }
}
```

`state` is the current aggregate loaded from the store (`null` if the stream does not exist yet). Throw to reject the command — no events will be appended.

### 2. Register

```csharp
using Winche.Events.Commands.DependencyInjection;

services.AddWincheEventsCommands(commands =>
{
    commands.AddHandler<PlaceOrderHandler>();
});
```

`AddHandler` infers `TCommand` and `TAggregate` from the `ICommandHandler<,>` interface the handler implements.

### 3. Dispatch

```csharp
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

var order = await dispatcher.DispatchAsync<Order>(
    "orders/123", new PlaceOrderCommand("orders/123", 49.99m));

// order reflects the state after the command's events have been applied
```

**Dispatch flow:**

1. Open a session
2. Load current aggregate state (`null` for new streams)
3. Call the registered handler → produce events
4. Append events and commit
5. Load and return the updated state

The command type is resolved at runtime from the registered handlers — no type parameter needed at the call site.

---

## Transaction isolation

`OpenSessionAsync` accepts an optional `IsolationLevel`:

```csharp
await using var session = await store.OpenSessionAsync(IsolationLevel.Serializable);
```

Default is `ReadCommitted`.

---

## Extending the base types

Aggregates and events are constrained to `IAggregate<string>` and `IEvent` respectively. The provided `Aggregate` and `Event` abstract records satisfy these constraints and are the recommended starting point. If your type already inherits from another class, implement the interface directly:

```csharp
public class MyOrder : ExistingBase, IAggregate<string>
{
    public string Id { get; init; } = string.Empty;
}

public class MyEvent : ExistingEventBase, IEvent { }
```

---

## Requirements

- .NET 10
- PostgreSQL (via Marten / Npgsql)
