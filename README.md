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

Projections fold events into an aggregate document. Register one handler per event type in the constructor using `On<TEvent>`. Handlers are always synchronous — if you need async work (external API calls, secondary DB lookups), do it in a command handler before producing events, and carry the result in the event payload. Unregistered event types are silently ignored.

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

Each handler receives `EventEnvelope<TEvent>` — access the event via `e.Data` and stream metadata via `e.Version`, `e.Timestamp`, `e.StreamId`, `e.Id`, `e.Sequence`.

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

| Mode | When document is built | `GetStateAsync` returns |
| --- | --- | --- |
| `Inline` | Inside the same transaction as the append | Always fresh after `SaveChangesAsync` |
| `Async` | Background daemon after commit | Eventually consistent |

**Inline** — use when you need the document to be immediately consistent after `SaveChangesAsync`. Projection handlers run inside the open PostgreSQL transaction — keep them fast with no external I/O.

**Async** — use when the projection is large or low-priority. The daemon processes events in the background; `GetStateAsync` may return stale state until it catches up. Use `LoadFreshAsync` when you need to wait for the daemon.

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

Waits for the async daemon to catch up to the latest committed event, then reads the document. Use this when you need guaranteed fresh state from an `Async` projection.

```csharp
var order = await session.LoadFreshAsync<Order>("orders/123");                    // default 5s timeout
var order = await session.LoadFreshAsync<Order>("orders/123", TimeSpan.FromSeconds(10));
```

### QueryStatesAsync

Queries stored aggregate documents using a LINQ compose function. The caller shapes the `IQueryable<TAggregate>` (filter, order, paging) before it is materialized.

```csharp
// All placed orders
var placed = await session.QueryStatesAsync<Order>(
    q => q.Where(o => o.Status == "placed"));

// Latest 10 orders, descending
var recent = await session.QueryStatesAsync<Order>(
    q => q.OrderByDescending(o => o.Id).Take(10));

// Filter + paging
var page = await session.QueryStatesAsync<Order>(
    q => q.Where(o => o.Status == "placed").OrderBy(o => o.Id).Skip(20).Take(10));
```

Pass `q => q` to return all documents of that type.

### GetStreamAsync

Returns a `StreamEnvelope<TAggregate>` combining the `mt_streams` row with the current projected aggregate document. Returns `null` if the stream does not exist.

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

Returns events for a stream in order, each wrapped as `EventEnvelope<IEvent>`. Pass `fromVersion` to fetch only events at or after that version.

```csharp
var events = await session.GetEventsAsync("orders/123");
var recent = await session.GetEventsAsync("orders/123", fromVersion: 5);

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

Projection handlers and `GetEventsAsync` work with `EventEnvelope<TEvent>`. It carries the full row from `mt_events`.

```csharp
public sealed record EventEnvelope<TEvent>(
    string Id,                // event UUID as string
    string StreamId,          // stream identifier
    TEvent Data,              // strongly-typed event payload
    long Version,             // 1-based position within the stream
    DateTimeOffset Timestamp, // when the event was committed (UTC)
    long Sequence,            // global sequence number across all streams
    string TypeAlias,         // value in the mt_events.type column
    string DotNetType         // value in the mt_events.dotnet_type column
) where TEvent : IEvent;
```

```csharp
On<OrderPlaced>((state, e) =>
{
    // e.Id          — event UUID (string)
    // e.Data        — strongly typed OrderPlaced
    // e.Version     — 1-based position in the stream
    // e.Timestamp   — when the event was committed (UTC)
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
    string Id,                   // stream identifier
    TAggregate? Aggregate,       // projected document (null if none stored yet)
    long Version,                // total events in the stream
    DateTimeOffset Created,      // when the stream was first written (UTC)
    DateTimeOffset LastModified, // when the stream last received an event (UTC)
    bool IsArchived,             // whether the stream is archived
    string AggregateType         // aggregate type name from mt_streams.type
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

`Command<TAggregate>` carries two built-in properties:

```csharp
// Set by the dispatcher automatically from the stream version at load time.
// Pass explicitly for optimistic concurrency — the server rejects if the stream
// has advanced since this version was observed.
long? ExpectedVersion { get; init; }   // null = no version check

// Populated automatically to DateTimeOffset.UtcNow at construction.
// Useful for audit trails and offline-sync conflict detection.
DateTimeOffset CreatedAt { get; init; }
```

```csharp
// Server-side dispatch with no version constraint (default):
await dispatcher.DispatchAsync("orders/123", new PlaceOrderCommand("orders/123", 49.99m));

// Explicit optimistic concurrency — reject if stream is not at version 2:
await dispatcher.DispatchAsync("orders/123", new ShipOrderCommand("orders/123") { ExpectedVersion = 2 });
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

**Async handlers with DI** — command handlers may perform async work (external API calls, secondary DB lookups) before producing events. Carry all enriched data in the event so projections remain pure synchronous transforms:

```csharp
class OrderCommandHandler(IInventoryService inventory) : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<ShipOrderCommand>(HandleShip);
    }

    private async Task<IEnumerable<IEvent>> HandleShip(Order? state, ShipOrderCommand cmd, CancellationToken ct)
    {
        var stock = await inventory.ReserveStockAsync(cmd.OrderId, ct);
        return [new OrderShipped(cmd.OrderId, reservedStock: stock)];
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

var result = await dispatcher.DispatchAsync("orders/123", new PlaceOrderCommand("orders/123", 49.99m));
```

`TAggregate` is inferred from the command's `Command<Order>` base — no explicit type argument needed.

`DispatchAsync` returns a `DispatchResult` containing the events the server appended and the new stream version:

```csharp
public sealed record DispatchResult(
    IReadOnlyList<EventEnvelope<IEvent>> Events,
    long Version);
```

```csharp
var result = await dispatcher.DispatchAsync("orders/123", new PlaceOrderCommand("orders/123", 49.99m));

result.Version          // new stream version after commit
result.Events           // full EventEnvelope<IEvent> for each appended event
result.Events[0].Id     // server-assigned event UUID
result.Events[0].Data   // the domain event (e.g. OrderPlaced)
```

Load the aggregate explicitly if you need state after dispatch:

```csharp
await using var session = await store.OpenSessionAsync();
var order = await session.GetStateAsync<Order>("orders/123");
```

**Dispatch flow:**

1. Open a session
2. Load current stream envelope via `GetStreamAsync` — captures aggregate state and previous version
3. Call the registered handler → produce events
4. Append events and commit
5. Fetch the newly appended events with full server metadata (`GetEventsAsync` from previous version + 1)
6. Return `DispatchResult`

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
