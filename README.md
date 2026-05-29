# Winche.Events

A [Marten](https://martendb.io/)-backed event sourcing library for .NET 10. Provides typed projections, an explicit unit-of-work session, optimistic concurrency, post-commit notifications, and an optional command-dispatch layer — all without exposing Marten types to your domain code.

---

## Packages

| Package | Purpose |
| --- | --- |
| `Winche.Events.Abstractions` | Base types: `IAggregate`, `Aggregate`, `IEvent`, `Event`, `ICommand<TAggregate>`, `Command<TAggregate>`, `EventEnvelope<TEvent>`, `StreamEnvelope<TAggregate>` |
| `Winche.Events` | Core: event store, sessions, projections, notifiers |
| `Winche.Events.Commands` | Optional: command handlers and dispatcher |

---

## Getting started

### 1. Define your domain model

```csharp
using Winche.Events.Abstractions;

record Order(string Status, decimal Total) : Aggregate
{
    public static Order Empty => new("none", 0);
}

record OrderPlaced(string OrderId, decimal Total) : Event;
record OrderShipped(string OrderId) : Event;
record OrderCancelled(string OrderId) : Event;
```

### 2. Define a projection

Projections fold events into an aggregate document. Register one handler per event type in the constructor using `On<TEvent>`. Unregistered event types are silently ignored.

```csharp
using Winche.Events.Projection;

class OrderProjection : Projection<Order>
{
    public OrderProjection()
    {
        On<OrderPlaced>((state, e) => state with { Status = "placed", Total = e.Data.Total });
        On<OrderShipped>((state, e) => state with { Status = "shipped" });
        On<OrderCancelled>((state, e) => state with { Status = "cancelled" });
    }

    public override Order Create(string id) => Order.Empty with { Id = id };
}
```

Each handler receives `EventEnvelope<TEvent>` — access the event via `e.Data` and stream metadata via `e.Version`, `e.Timestamp`, `e.StreamId`.

**Private methods as handlers:**

```csharp
class OrderProjection : Projection<Order>
{
    public OrderProjection()
    {
        On<OrderPlaced>(HandlePlaced);
        On<OrderShipped>((state, e) => state with { Status = "shipped" });
    }

    public override Order Create(string id) => Order.Empty with { Id = id };

    private Order HandlePlaced(Order state, EventEnvelope<OrderPlaced> e)
        => state with { Status = "placed", Total = e.Data.Total };
}
```

**Async handlers with DI:**

Constructor arguments are resolved from DI. Lambdas close over them.

```csharp
class OrderProjection(IInventoryClient inventory) : Projection<Order>
{
    public OrderProjection()
    {
        On<OrderShipped>(HandleShipped);
    }

    public override Order Create(string id) => Order.Empty with { Id = id };

    private async Task<Order> HandleShipped(Order state, EventEnvelope<OrderShipped> e)
    {
        var stock = await inventory.GetStockAsync(e.Data.OrderId);
        return state with { Status = "shipped", Stock = stock };
    }
}
```

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

    opts.AddProjection<OrderProjection, Order>(ProjectionMode.Inline);
});
```

### 4. Use the event store

```csharp
await using var session = await store.OpenSessionAsync();

await session.AppendStreamAsync("orders/123", [new OrderPlaced("orders/123", 49.99m)]);
await session.SaveChangesAsync();

var order = await session.GetStateAsync<Order>("orders/123");
// order.Status == "placed"
```

---

## Projection modes

| Mode | When document is built | `LoadAsync` returns | Async handlers safe? |
| --- | --- | --- | --- |
| `Inline` | Inside the same transaction as the append | Always fresh | No — runs inside a DB transaction |
| `Async` | Background daemon after commit | Eventually consistent | Yes |

**Inline** — use when you need the document to be immediately consistent after `SaveChangesAsync`. Handlers must be fast and synchronous (no external I/O — they run inside the open PostgreSQL transaction).

**Async** — use when handlers need to call external APIs or other databases. The daemon processes events in the background; `LoadAsync` may return stale state until it catches up.

```csharp
opts.AddProjection<OrderProjection, Order>(ProjectionMode.Inline);
opts.AddProjection<OrderReadModel, OrderSummary>(ProjectionMode.Async);
```

---

## IEventSession

`IEventSession` is a unit of work scoped to a single PostgreSQL connection. Always dispose with `await using`.

### AppendStreamAsync

```csharp
await session.AppendStreamAsync("orders/123", [new OrderPlaced("orders/123", 49.99m)]);
```

**Optimistic concurrency** — pass `expectedVersion` to reject concurrent writes:

```csharp
await session.AppendStreamAsync("orders/123", events, expectedVersion: 3);
// Marten throws if the stream's current version doesn't match.
```

### GetStateAsync

Reads the stored aggregate document. For `Inline` projections this is always fresh after commit. For `Async` projections it reflects the last time the daemon ran.

```csharp
var order = await session.GetStateAsync<Order>("orders/123");
```

### LoadFreshAsync

Waits for the async daemon to catch up to the latest committed event, then reads the document. Use this after `SaveChangesAsync` when you need guaranteed fresh state from an `Async` projection.

```csharp
var order = await session.LoadFreshAsync<Order>("orders/123");                    // default 5s timeout
var order = await session.LoadFreshAsync<Order>("orders/123", TimeSpan.FromSeconds(10));
```

### GetStreamAsync

Returns a `StreamEnvelope<TAggregate>` combining the stream row from `mt_streams` with the current projected aggregate document. Returns `null` if the stream does not exist.

```csharp
var envelope = await session.GetStreamAsync<Order>("orders/123");

if (envelope is not null)
{
    // envelope.Id           — stream identifier
    // envelope.Aggregate    — current projected document (null if no projection stored yet)
    // envelope.Version      — total events appended
    // envelope.Created      — when the stream was first written
    // envelope.LastModified — when the stream last received an event
    // envelope.IsArchived   — whether the stream is archived
    // envelope.AggregateType — aggregate type name from mt_streams
}
```

### GetEventsAsync

Returns all events for a stream in order, each wrapped as `EventEnvelope<IEvent>`.

```csharp
var events = await session.GetEventsAsync("orders/123");

foreach (var e in events)
    Console.WriteLine($"v{e.Version} [{e.Timestamp:u}] {e.Data.GetType().Name}");
```

Use `OfEventType<TEvent>()` to filter and cast to a specific event type:

```csharp
var placed = events.OfEventType<OrderPlaced>();
// Each element is EventEnvelope<OrderPlaced> with strongly-typed e.Data
```

### SaveChangesAsync

Commits all buffered appends to PostgreSQL, then fires registered notifiers.

```csharp
await session.SaveChangesAsync();
```

---

## EventEnvelope\<TEvent\>

Projection handlers receive `EventEnvelope<TEvent>` rather than the raw event. It carries the full row from `mt_events`.

```csharp
public sealed record EventEnvelope<TEvent>(
    string Id,           // event UUID as string
    string StreamId,     // stream identifier
    TEvent Data,         // strongly-typed event payload
    long Version,        // 1-based position within the stream
    DateTimeOffset Timestamp, // when the event was committed (UTC)
    long Sequence,       // global sequence number across all streams
    string TypeAlias,    // value in the mt_events.type column
    string DotNetType    // value in the mt_events.dotnet_type column
) where TEvent : IEvent;
```

```csharp
On<OrderPlaced>((state, e) =>
{
    // e.Id          — event UUID (string)
    // e.Data        — strongly typed OrderPlaced
    // e.Version     — position in the stream (1-based)
    // e.Timestamp   — when the event was committed
    // e.StreamId    — the stream identifier
    // e.Sequence    — global order across all streams
    // e.TypeAlias   — e.g. "order_placed"
    // e.DotNetType  — e.g. "MyApp.OrderPlaced, MyApp"
    return state with { Status = "placed", Total = e.Data.Total };
});
```

## StreamEnvelope\<TAggregate\>

Returned by `GetStreamAsync`. Combines the `mt_streams` row with the current projected document.

```csharp
public sealed record StreamEnvelope<TAggregate>(
    string Id,                  // stream identifier
    TAggregate? Aggregate,      // projected document (null if none stored yet)
    long Version,               // total events in the stream
    DateTimeOffset Created,     // when the stream was first written (UTC)
    DateTimeOffset LastModified,// when the stream last received an event (UTC)
    bool IsArchived,            // whether the stream is archived
    string AggregateType        // aggregate type name from mt_streams.type
) where TAggregate : class, IAggregate;
```

---

## Post-commit notifications

Implement `IAppendNotifier` to receive a callback after each successful commit:

```csharp
using Winche.Events.Notification;

class OrderNotifier : IAppendNotifier
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
opts.AddNotifier<OrderNotifier>();
```

Multiple notifiers can be registered. An exception in one is logged and swallowed; it does not affect the others or the caller.

---

## Commands (Winche.Events.Commands)

The commands package adds a load → handle → append → commit dispatch loop on top of `IEventSession`.

### 1. Define commands

Commands extend `Command<TAggregate>` (or implement `ICommand<TAggregate>`). The aggregate type is encoded in the type, enabling zero-boilerplate dispatch.

```csharp
using Winche.Events.Abstractions;

record PlaceOrderCommand(string OrderId, decimal Total) : Command<Order>;
record ShipOrderCommand(string OrderId) : Command<Order>;
```

### 2. Define a command handler

All commands for an aggregate live in one `CommandHandler<TAggregate>` class, registered with `On<TCommand>` in the constructor — symmetric to how projections work.

```csharp
using Winche.Events.Commands;

class OrderCommandHandler : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<PlaceOrderCommand>((state, cmd) =>
        {
            if (state is { Status: not "none" })
                throw new InvalidOperationException("Order already exists.");
            return [new OrderPlaced(cmd.OrderId, cmd.Total)];
        });

        On<ShipOrderCommand>((state, cmd) =>
        {
            if (state is null or { Status: "none" })
                throw new InvalidOperationException("Order does not exist.");
            return [new OrderShipped(cmd.OrderId)];
        });
    }
}
```

`state` is the current aggregate loaded from the store (`null` if the stream does not exist). Throw to reject the command — no events will be appended.

Async handlers and DI injection work the same way as projections:

```csharp
class OrderCommandHandler(IInventoryService inventory) : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<ShipOrderCommand>(HandleShip);
    }

    private async Task<IEnumerable<IEvent>> HandleShip(Order? state, ShipOrderCommand cmd)
    {
        await inventory.ReserveStockAsync(cmd.OrderId);
        return [new OrderShipped(cmd.OrderId)];
    }
}
```

### 3. Register

```csharp
using Winche.Events.Commands.DependencyInjection;

services.AddWincheEventsCommands(cmds =>
{
    cmds.AddCommandHandler<OrderCommandHandler, Order>();
});
```

### 4. Dispatch

```csharp
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

await dispatcher.DispatchAsync("orders/123", new PlaceOrderCommand("orders/123", 49.99m));
```

`TAggregate` is inferred from the command's `Command<Order>` base — no explicit type argument needed.

`DispatchAsync` does not return state. Load it explicitly if needed:

```csharp
await dispatcher.DispatchAsync("orders/123", new PlaceOrderCommand("orders/123", 49.99m));

await using var session = await store.OpenSessionAsync();
var order = await session.GetStateAsync<Order>("orders/123");
```

**Dispatch flow:**

1. Open a session
2. Load current aggregate state via `LoadAsync` (`null` for new streams)
3. Call the registered handler → produce events
4. Append events and commit

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
