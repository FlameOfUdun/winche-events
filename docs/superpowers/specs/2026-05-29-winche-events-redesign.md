# Winche.Events Redesign

**Date:** 2026-05-29
**Scope:** Full redesign of Winche.Events, Winche.Events.Commands, Winche.Events.Abstractions

---

## Goals

- Zero runtime reflection or `dynamic` in hot paths
- Zero `MakeGenericType` / `MethodInfo.Invoke` anywhere in dispatch
- All type information captured at compile time via explicit generic parameters
- Symmetric `On<T>` registration pattern for both projections and command handlers
- `ICommandDispatcher` has a single responsibility: dispatch
- `LoadAsync` reads the stored document — no event replay on every call
- Support both Inline and Async projection modes as a per-registration decision
- Projection handlers receive a typed `EventEnvelope<TEvent>` giving access to event data and stream metadata

---

## What Changes

### 1. Projection dispatch — `On<TEvent>`

`Projection<TAggregate>` replaces convention-based `Apply` / `ApplyAsync` method discovery with explicit delegate registration in the constructor. All `ConcurrentDictionary` reflection caches and `MethodInfo.Invoke` calls are deleted.

#### `EventEnvelope<TEvent>`

Handlers receive a generic envelope carrying both the typed event data and stream metadata. Defined in `Winche.Events.Abstractions`:

```csharp
public sealed record EventEnvelope<TEvent>(
    string StreamId,
    TEvent Data,
    long Version,
    DateTimeOffset Timestamp) where TEvent : IEvent;
```

The existing non-generic `EventEnvelope` (used by `GetEventsAsync`) is replaced by `EventEnvelope<IEvent>` everywhere, unifying the type.

#### `On<TEvent>` overloads

```csharp
protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, TAggregate> handler)
    where TEvent : IEvent

protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, Task<TAggregate>> handler)
    where TEvent : IEvent
```

Both store a typed closure in a `Dictionary<Type, Func<TAggregate, EventEnvelope<IEvent>, Task<TAggregate>>>` keyed by `TEvent`. The closure upcasts `EventEnvelope<IEvent>` to `EventEnvelope<TEvent>` — safe because the dictionary is keyed by the same type. `ApplyEventAsync` becomes a plain `TryGetValue` — no reflection.

`ProjectionBridge` maps the JasperFx `IEvent` wrapper to `EventEnvelope<IEvent>` before calling `ApplyEventAsync`.

Private methods can be passed as method groups:

```csharp
class OrderProjection(IInventoryClient inventory) : Projection<Order>
{
    public OrderProjection()
    {
        On<OrderPlaced>((s, e) => s with { Status = "placed", Total = e.Data.Total });
        On<OrderShipped>(HandleShipped);
    }

    public override Order Create(string id) => new("none", 0) { Id = id };

    private async Task<Order> HandleShipped(Order s, EventEnvelope<OrderShipped> e)
    {
        var stock = await inventory.GetStockAsync(e.Data.ProductId);
        return s with { Status = "shipped", Stock = stock, LastModified = e.Timestamp };
    }
}
```

DI dependencies are injected via the constructor. The `On<TEvent>` lambdas close over them. Works because projections are registered as singletons with constructor injection (no `new()` constraint).

---

### 2. Command handling — `CommandHandler<TAggregate>`

`ICommandHandler<TCommand, TAggregate>` and `ICommandHandlerInvoker` are deleted. Replaced by a `CommandHandler<TAggregate>` base class following the same `On<T>` pattern. All commands for an aggregate live in one class.

Three overloads:

```csharp
protected void On<TCommand>(Func<TAggregate?, TCommand, IEnumerable<IEvent>> handler)
    where TCommand : ICommand<TAggregate>

protected void On<TCommand>(Func<TAggregate?, TCommand, Task<IEnumerable<IEvent>>> handler)
    where TCommand : ICommand<TAggregate>

protected void On<TCommand>(Func<TAggregate?, TCommand, CancellationToken, Task<IEnumerable<IEvent>>> handler)
    where TCommand : ICommand<TAggregate>
```

Internal `HandleAsync(TAggregate? state, object command, CancellationToken ct)` does a `TryGetValue` lookup by `command.GetType()` and invokes the delegate. No `dynamic`, no `MakeGenericType`.

```csharp
class OrderCommandHandler(IInventoryService inv) : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<PlaceOrderCommand>((s, cmd) =>
        {
            if (s is { Status: not "none" }) throw new InvalidOperationException("Already exists.");
            return [new OrderPlaced(cmd.OrderId, cmd.Total)];
        });
        On<ShipOrderCommand>(HandleShip);
    }

    private async Task<IEnumerable<IEvent>> HandleShip(Order? s, ShipOrderCommand cmd)
    {
        await inv.ReserveStockAsync(cmd.OrderId);
        return [new OrderShipped(cmd.OrderId)];
    }
}
```

---

### 3. Registration — explicit type params, no startup reflection

`ProjectionMode` is reinstated. Both mode and types are explicit at registration — no type hierarchy walking, no `MakeGenericMethod`, no MethodInfo statics.

**Projections:**

```csharp
opts.AddProjection<OrderProjection, Order>(ProjectionMode.Inline);
opts.AddProjection<OrderReadModelProjection, OrderReadModel>(ProjectionMode.Async);
```

Internally captures two typed closures (DI registration + Marten config) right in the generic method — both `TProjection` and `TAggregate` are compile-time constants at that point.

**Command handlers:**

```csharp
cmds.AddCommandHandler<OrderCommandHandler, Order>();
```

Internally: `services.AddSingleton<CommandHandler<Order>, OrderCommandHandler>()`. Nothing else needed.

**Developer responsibility:** registering a projection with external DB reads as `Inline` holds the PostgreSQL transaction open during those awaits. That tradeoff is the developer's to own.

---

### 4. `ICommandDispatcher` — single method, `Task` return

```csharp
public interface ICommandDispatcher
{
    Task DispatchAsync<TAggregate>(
        string streamId,
        ICommand<TAggregate> command,
        long? expectedVersion = null,
        CancellationToken ct = default)
        where TAggregate : class, IAggregate;
}
```

`TAggregate` is inferred from the command's `ICommand<TAggregate>` implementation — no explicit type args at the call site.

`DispatchAsync` does not return state. Consumers who need state after dispatch open a session and call `LoadAsync` themselves, making the cost explicit:

```csharp
await dispatcher.DispatchAsync(streamId, new PlaceOrderCommand(...));

// only pay for the read if you need it
await using var session = await eventStore.OpenSessionAsync();
var order = await session.LoadAsync<Order>(streamId);
```

The `CommandDispatcher` implementation:

```csharp
public async Task DispatchAsync<TAggregate>(
    string streamId, ICommand<TAggregate> command,
    long? expectedVersion = null, CancellationToken ct = default)
    where TAggregate : class, IAggregate
{
    await using var session = await _eventStore.OpenSessionAsync(ct: ct);
    var state   = await session.LoadAsync<TAggregate>(streamId, ct);
    var handler = _serviceProvider.GetRequiredService<CommandHandler<TAggregate>>();
    var events  = await handler.HandleAsync(state, command, ct);
    await session.AppendAsync(streamId, events, expectedVersion, ct);
    await session.SaveChangesAsync(ct);
}
```

No `MakeGenericType`, no `dynamic`, no `MethodInfo`.

---

### 5. `LoadAsync` — stored document, no event replay

`EventSession.LoadAsync<TAggregate>` reads the Marten-stored document directly:

```csharp
public Task<TAggregate?> LoadAsync<TAggregate>(string streamId, CancellationToken ct = default)
    where TAggregate : class, IAggregate
    => _session.LoadAsync<TAggregate>(streamId, ct);
```

- **Inline projection:** document is updated in the same transaction as the append — always fresh after commit.
- **Async projection:** document is updated by the daemon — eventually consistent.

`EventSession` no longer needs `IServiceProvider` (previously used for event replay). The constructor is simplified and `EventStore` no longer passes it through.

---

## Event Flow (Inline mode)

```text
dispatcher.DispatchAsync(orderId, new ShipOrderCommand(...))
  │
  ├─ session.LoadAsync<Order>      → reads stored Order doc from PostgreSQL
  ├─ CommandHandler<Order>
  │   └─ On<ShipOrderCommand>      → validates state, returns [OrderShipped]
  ├─ session.AppendAsync           → buffers event
  └─ session.SaveChangesAsync      → commits transaction
      └─ Marten inline projection  → On<OrderShipped> runs in same transaction
          └─ updated Order doc written to PostgreSQL

[request returns — stored doc is fresh]
```

## Event Flow (Async mode)

```text
dispatcher.DispatchAsync(orderId, new ShipOrderCommand(...))
  │
  ├─ session.LoadAsync<Order>      → reads stored Order doc (may be slightly stale)
  ├─ CommandHandler<Order>
  │   └─ On<ShipOrderCommand>      → validates state, returns [OrderShipped]
  ├─ session.AppendAsync           → buffers event
  └─ session.SaveChangesAsync      → commits — doc NOT yet updated

[request returns]

[Marten daemon, background]
  └─ On<OrderShipped> runs
      ├─ await inventoryDb.GetStock(...)   ← external read safe here
      └─ updated Order doc written to PostgreSQL
```

---

## What Is Deleted

| Deleted | Reason |
| --- | --- |
| `ICommandHandler<TCommand, TAggregate>` | Replaced by `CommandHandler<TAggregate>` |
| `ICommandHandlerInvoker` | Was a band-aid for `dynamic` dispatch |
| `ConcurrentDictionary` caches in `Projection<TAggregate>` | Replaced by constructor-built dictionaries |
| `MethodInfo` / `BindingFlags` in `Projection<TAggregate>` | `On<TEvent>` delegates need none |
| `FindAggregateType` | Type params are now explicit |
| `_configureForMartenMethod` MethodInfo static | Type params are now explicit |
| `MakeGenericMethod` in `ServiceCollectionExtensions` | Type params are now explicit |
| `WincheEventsCommandsOptions.AddHandler<THandler>()` | Replaced by `AddCommandHandler<THandler, TAggregate>()` |
| `dynamic` in `CommandDispatcher` | Replaced by typed `CommandHandler<TAggregate>` lookup |
| `IServiceProvider` in `EventSession` | No longer needed (no event replay in `LoadAsync`) |
| `DispatchAndLoadAsync` | Consumer calls `LoadAsync` explicitly |
| Non-generic `EventEnvelope` | Replaced by `EventEnvelope<IEvent>` / `EventEnvelope<TEvent>` |

---

## Consumer API — Before vs After

**Projection:**

```csharp
// before
class OrderProjection : Projection<Order>
{
    public override Order Create(string id) => ...;
    public Order Apply(Order s, OrderPlaced e) => s with { Status = "placed" };
    public async Task<Order> ApplyAsync(Order s, OrderShipped e) { ... }
}

// after
class OrderProjection(IInventoryClient inv) : Projection<Order>
{
    public OrderProjection()
    {
        On<OrderPlaced>((s, e) => s with { Status = "placed", Total = e.Data.Total });
        On<OrderShipped>(HandleShipped);
    }
    public override Order Create(string id) => ...;
    private async Task<Order> HandleShipped(Order s, EventEnvelope<OrderShipped> e)
    {
        var stock = await inv.GetStockAsync(e.Data.ProductId);
        return s with { Status = "shipped", LastModified = e.Timestamp };
    }
}
```

**Command handler:**

```csharp
// before — one class per command
class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Order> { ... }
class ShipOrderHandler  : ICommandHandler<ShipOrderCommand, Order>  { ... }

// after — one class per aggregate
class OrderCommandHandler : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<PlaceOrderCommand>((s, cmd) => [...]);
        On<ShipOrderCommand>(HandleShip);
    }
}
```

**Registration:**

```csharp
// before
opts.AddProjection<OrderProjection>();
cmds.AddHandler<PlaceOrderHandler>();
cmds.AddHandler<ShipOrderHandler>();

// after
opts.AddProjection<OrderProjection, Order>(ProjectionMode.Inline);
cmds.AddCommandHandler<OrderCommandHandler, Order>();
```

**Dispatch:**

```csharp
// before
var order = await dispatcher.DispatchAsync<Order>(streamId, new PlaceOrderCommand(...));

// after
await dispatcher.DispatchAsync(streamId, new PlaceOrderCommand(...));
// load explicitly if needed
var order = await session.LoadAsync<Order>(streamId);
```
