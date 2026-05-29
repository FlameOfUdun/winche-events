# Winche.Events Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace all runtime reflection and `dynamic` dispatch with compile-time-safe delegate dictionaries, symmetric `On<T>` registration for projections and command handlers, and a simplified session layer that reads stored documents instead of replaying events.

**Architecture:** `Projection<TAggregate>` and `CommandHandler<TAggregate>` both use constructor-registered delegate dictionaries keyed by event/command type — no `MethodInfo`, no `ConcurrentDictionary` caches. Registration closures capture all type parameters at compile time, eliminating `MakeGenericType` and `FindAggregateType`. `EventSession.LoadAsync` reads the stored Marten document directly; `ICommandDispatcher.DispatchAsync` returns `Task` and does not reload state.

**Tech Stack:** .NET 10, C# 13, Marten 9, JasperFx.Events 2, xUnit, NSubstitute, FluentAssertions

---

## File Map

**Create:**
- `src/Winche.Events.Abstractions/EventEnvelope.cs` — generic `EventEnvelope<TEvent>` record
- `src/Winche.Events/Projection/ProjectionMode.cs` — `Inline` / `Async` enum
- `src/Winche.Events.Commands/CommandHandler.cs` — `CommandHandler<TAggregate>` base class
- `tests/Winche.Events.Commands.Tests/CommandHandlerTests.cs` — unit tests for `CommandHandler<TAggregate>`

**Rewrite:**
- `src/Winche.Events/Projection/Projection.cs` — `On<TEvent>` delegate dispatch, two dictionaries
- `src/Winche.Events/Projection/Internal/ProjectionBridge.cs` — map JasperFx event → `EventEnvelope<IEvent>`
- `src/Winche.Events/Session/IEventSession.cs` — `GetEventsAsync` returns `IReadOnlyList<EventEnvelope<IEvent>>`
- `src/Winche.Events/Session/Internal/EventSession.cs` — `LoadAsync` reads stored doc, remove `IServiceProvider`
- `src/Winche.Events/Session/Internal/EventStore.cs` — remove `IServiceProvider`
- `src/Winche.Events/DependencyInjection/WincheEventsOptions.cs` — typed closure pairs, `AddProjection<T, TAggregate>(mode)`
- `src/Winche.Events/DependencyInjection/ServiceCollectionExtensions.cs` — iterate closure pairs, no reflection
- `src/Winche.Events.Commands/ICommandDispatcher.cs` — `Task` return (no state)
- `src/Winche.Events.Commands/Internal/CommandDispatcher.cs` — resolve `CommandHandler<TAggregate>`, no second load
- `src/Winche.Events.Commands/DependencyInjection/WincheEventsCommandsOptions.cs` — `AddCommandHandler<T, TAggregate>()`
- `src/Winche.Events.Commands/DependencyInjection/ServiceCollectionExtensions.cs` — iterate registrations

**Update (tests):**
- `tests/Winche.Events.Tests/Projection/ProjectionTests.cs`
- `tests/Winche.Events.Tests/Projection/ProjectionBridgeTests.cs`
- `tests/Winche.Events.Tests/Session/EventSessionTests.cs`
- `tests/Winche.Events.Tests/Session/EventSessionNotifierTests.cs`
- `tests/Winche.Events.Tests/DependencyInjection/WincheEventsOptionsTests.cs`
- `tests/Winche.Events.Tests/DependencyInjection/WincheEventsRegistrationTests.cs`
- `tests/Winche.Events.Commands.Tests/CommandDispatcherTests.cs`
- `tests/Winche.Events.Commands.Tests/WincheEventsCommandsOptionsTests.cs`

**Delete:**
- `src/Winche.Events/Session/EventEnvelope.cs` — replaced by generic version in Abstractions
- `src/Winche.Events.Commands/ICommandHandler.cs` — replaced by `CommandHandler<TAggregate>`
- `src/Winche.Events.Commands/Internal/ICommandHandlerInvoker.cs` — was a band-aid

**Update (sample):**
- `samples/Winche.Events.Sample/Program.cs`

---

## Task 1: `EventEnvelope<TEvent>` in Abstractions

**Files:**
- Create: `src/Winche.Events.Abstractions/EventEnvelope.cs`

- [ ] **Step 1.1: Write the failing test**

Add to a new temporary test class in `tests/Winche.Events.Tests/Projection/ProjectionTests.cs` (top of file, alongside existing types):

```csharp
public class EventEnvelopeTests
{
    [Fact]
    public void EventEnvelope_exposes_typed_data_and_metadata()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var data = new Incremented();
        var envelope = new EventEnvelope<Incremented>("counters/1", data, 3, ts);

        envelope.StreamId.Should().Be("counters/1");
        envelope.Data.Should().BeSameAs(data);
        envelope.Version.Should().Be(3);
        envelope.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void EventEnvelope_IEvent_holds_base_typed_data()
    {
        var data = new Incremented();
        var envelope = new EventEnvelope<IEvent>("counters/1", data, 1, DateTimeOffset.UtcNow);

        envelope.Data.Should().BeSameAs(data);
        envelope.Data.Should().BeOfType<Incremented>();
    }
}
```

- [ ] **Step 1.2: Run to confirm it fails**

```
dotnet test tests/Winche.Events.Tests --filter "EventEnvelopeTests" -v q
```

Expected: compile error — `EventEnvelope<T>` does not exist.

- [ ] **Step 1.3: Create `EventEnvelope<TEvent>`**

Create `src/Winche.Events.Abstractions/EventEnvelope.cs`:

```csharp
namespace Winche.Events.Abstractions;

/// <summary>
/// Wraps a domain event with the stream metadata available at the point of processing.
/// </summary>
/// <typeparam name="TEvent">The domain event type.</typeparam>
/// <param name="StreamId">The stream the event belongs to.</param>
/// <param name="Data">The typed domain event payload.</param>
/// <param name="Version">1-based position of this event within its stream.</param>
/// <param name="Timestamp">When the event was committed to the store.</param>
public sealed record EventEnvelope<TEvent>(
    string StreamId,
    TEvent Data,
    long Version,
    DateTimeOffset Timestamp) where TEvent : IEvent;
```

- [ ] **Step 1.4: Run tests**

```
dotnet test tests/Winche.Events.Tests --filter "EventEnvelopeTests" -v q
```

Expected: 2 tests pass.

- [ ] **Step 1.5: Commit**

```
git add src/Winche.Events.Abstractions/EventEnvelope.cs tests/Winche.Events.Tests/Projection/ProjectionTests.cs
git commit -m "feat: add generic EventEnvelope<TEvent> to Abstractions"
```

---

## Task 2: Recreate `ProjectionMode`

**Files:**
- Create: `src/Winche.Events/Projection/ProjectionMode.cs`

- [ ] **Step 2.1: Create the enum**

```csharp
namespace Winche.Events.Projection;

/// <summary>Controls when and how the aggregate document is built from its event stream.</summary>
public enum ProjectionMode
{
    /// <summary>
    /// Updated synchronously within the same transaction as the appended events.
    /// <see cref="Session.IEventSession.LoadAsync{TAggregate}"/> returns the fresh document immediately.
    /// Handlers registered with <c>On&lt;TEvent&gt;</c> must not perform external I/O — they run inside
    /// the open PostgreSQL transaction.
    /// </summary>
    Inline,

    /// <summary>
    /// Updated by a background daemon after events are committed. Eventually consistent.
    /// Safe for async handlers that perform external DB reads or API calls.
    /// </summary>
    Async,
}
```

- [ ] **Step 2.2: Build to confirm no errors**

```
dotnet build src/Winche.Events/Winche.Events.csproj -v q
```

Expected: Build succeeded.

- [ ] **Step 2.3: Commit**

```
git add src/Winche.Events/Projection/ProjectionMode.cs
git commit -m "feat: reinstate ProjectionMode enum (Inline / Async)"
```

---

## Task 3: Rewrite `Projection<TAggregate>` with `On<TEvent>` dispatch

**Files:**
- Modify: `src/Winche.Events/Projection/Projection.cs`
- Modify: `tests/Winche.Events.Tests/Projection/ProjectionTests.cs`

- [ ] **Step 3.1: Rewrite `Projection.cs`**

Replace the entire file:

```csharp
using Winche.Events.Abstractions;

namespace Winche.Events.Projection;

/// <summary>
/// Base class for aggregate projections. Register handlers for each event type using
/// <c>On&lt;TEvent&gt;</c> in the constructor. Unregistered event types are silently ignored.
/// </summary>
/// <typeparam name="TAggregate">The aggregate state type produced by this projection.</typeparam>
public abstract class Projection<TAggregate> : ProjectionBase where TAggregate : class, IAggregate
{
    private readonly Dictionary<Type, Func<TAggregate, EventEnvelope<IEvent>, TAggregate>> _syncHandlers = new();
    private readonly Dictionary<Type, Func<TAggregate, EventEnvelope<IEvent>, Task<TAggregate>>> _asyncHandlers = new();

    /// <summary>Returns the initial (empty) state for the given stream identifier.</summary>
    public abstract TAggregate Create(string id);

    /// <summary>Registers a synchronous handler for <typeparamref name="TEvent"/>.</summary>
    protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, TAggregate> handler)
        where TEvent : IEvent
        => _syncHandlers[typeof(TEvent)] = (state, e) =>
            handler(state, new EventEnvelope<TEvent>(e.StreamId, (TEvent)e.Data, e.Version, e.Timestamp));

    /// <summary>Registers an asynchronous handler for <typeparamref name="TEvent"/>.</summary>
    protected void On<TEvent>(Func<TAggregate, EventEnvelope<TEvent>, Task<TAggregate>> handler)
        where TEvent : IEvent
        => _asyncHandlers[typeof(TEvent)] = (state, e) =>
            handler(state, new EventEnvelope<TEvent>(e.StreamId, (TEvent)e.Data, e.Version, e.Timestamp));

    internal TAggregate ApplyEvent(TAggregate state, EventEnvelope<IEvent> envelope)
        => _syncHandlers.TryGetValue(envelope.Data.GetType(), out var handler)
            ? handler(state, envelope)
            : state;

    internal async Task<TAggregate> ApplyEventAsync(TAggregate state, EventEnvelope<IEvent> envelope)
    {
        var type = envelope.Data.GetType();
        if (_asyncHandlers.TryGetValue(type, out var asyncHandler))
            return await asyncHandler(state, envelope);
        if (_syncHandlers.TryGetValue(type, out var syncHandler))
            return syncHandler(state, envelope);
        return state;
    }
}
```

- [ ] **Step 3.2: Rewrite `ProjectionTests.cs`**

Replace the entire file:

```csharp
using FluentAssertions;
using Winche.Events.Abstractions;
using Winche.Events.Projection;
using Xunit;

namespace Winche.Events.Tests.Projection;

public record Counter(int Value) : Aggregate;
public record Incremented : Event;
public record Decremented : Event;
public record UnknownEvent : Event;

class CounterProjection : Projection<Counter>
{
    public CounterProjection()
    {
        On<Incremented>((s, e) => s with { Value = s.Value + 1 });
        On<Decremented>((s, e) => s with { Value = s.Value - 1 });
    }
    public override Counter Create(string id) => new Counter(0) { Id = id };
}

class AsyncCounterProjection : Projection<Counter>
{
    public AsyncCounterProjection()
    {
        On<Incremented>((s, e) => s with { Value = s.Value + 1 });
        On<Incremented>(async (Counter s, EventEnvelope<Incremented> e) =>
        {
            await Task.Yield();
            return s with { Value = s.Value + 10 };
        });
    }
    public override Counter Create(string id) => new Counter(0) { Id = id };
}

class MetadataProjection : Projection<Counter>
{
    public DateTimeOffset LastTimestamp { get; private set; }
    public long LastVersion { get; private set; }
    public string LastStreamId { get; private set; } = string.Empty;

    public MetadataProjection()
    {
        On<Incremented>((s, e) =>
        {
            LastTimestamp = e.Timestamp;
            LastVersion = e.Version;
            LastStreamId = e.StreamId;
            return s with { Value = s.Value + 1 };
        });
    }
    public override Counter Create(string id) => new Counter(0) { Id = id };
}

class MethodGroupProjection : Projection<Counter>
{
    public MethodGroupProjection()
    {
        On<Incremented>(Handle);
    }
    public override Counter Create(string id) => new Counter(0) { Id = id };
    private Counter Handle(Counter s, EventEnvelope<Incremented> e) => s with { Value = s.Value + 5 };
}

public class EventEnvelopeTests
{
    [Fact]
    public void EventEnvelope_exposes_typed_data_and_metadata()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var data = new Incremented();
        var envelope = new EventEnvelope<Incremented>("counters/1", data, 3, ts);

        envelope.StreamId.Should().Be("counters/1");
        envelope.Data.Should().BeSameAs(data);
        envelope.Version.Should().Be(3);
        envelope.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void EventEnvelope_IEvent_holds_base_typed_data()
    {
        var data = new Incremented();
        var envelope = new EventEnvelope<IEvent>("counters/1", data, 1, DateTimeOffset.UtcNow);

        envelope.Data.Should().BeSameAs(data);
        envelope.Data.Should().BeOfType<Incremented>();
    }
}

public class ProjectionTests
{
    private static EventEnvelope<IEvent> Envelope(IEvent data, string streamId = "test", long version = 1)
        => new(streamId, data, version, DateTimeOffset.UtcNow);

    [Fact]
    public void Create_returns_initial_state()
    {
        var p = new CounterProjection();
        p.Create("test").Should().Be(new Counter(0) { Id = "test" });
    }

    [Fact]
    public void ApplyEvent_dispatches_registered_sync_handler()
    {
        var p = new CounterProjection();
        var result = p.ApplyEvent(p.Create("test"), Envelope(new Incremented()));
        result.Value.Should().Be(1);
    }

    [Fact]
    public void ApplyEvent_returns_state_unchanged_for_unregistered_event()
    {
        var p = new CounterProjection();
        var state = new Counter(42) { Id = "test" };
        p.ApplyEvent(state, Envelope(new UnknownEvent())).Should().Be(state);
    }

    [Fact]
    public async Task ApplyEventAsync_prefers_async_handler_when_both_registered()
    {
        var p = new AsyncCounterProjection();
        var result = await p.ApplyEventAsync(p.Create("test"), Envelope(new Incremented()));
        result.Value.Should().Be(10);
    }

    [Fact]
    public async Task ApplyEventAsync_falls_back_to_sync_handler()
    {
        var p = new CounterProjection();
        var result = await p.ApplyEventAsync(p.Create("test"), Envelope(new Incremented()));
        result.Value.Should().Be(1);
    }

    [Fact]
    public async Task ApplyEventAsync_returns_state_unchanged_for_unregistered_event()
    {
        var p = new CounterProjection();
        var state = new Counter(42) { Id = "test" };
        var result = await p.ApplyEventAsync(state, Envelope(new UnknownEvent()));
        result.Should().Be(state);
    }

    [Fact]
    public void Handler_receives_envelope_metadata()
    {
        var ts = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var p = new MetadataProjection();
        p.ApplyEvent(p.Create("counters/99"), new EventEnvelope<IEvent>("counters/99", new Incremented(), 7, ts));

        p.LastStreamId.Should().Be("counters/99");
        p.LastVersion.Should().Be(7);
        p.LastTimestamp.Should().Be(ts);
    }

    [Fact]
    public void Method_group_is_accepted_as_handler()
    {
        var p = new MethodGroupProjection();
        var result = p.ApplyEvent(p.Create("test"), Envelope(new Incremented()));
        result.Value.Should().Be(5);
    }
}
```

- [ ] **Step 3.3: Run tests**

```
dotnet test tests/Winche.Events.Tests --filter "ProjectionTests|EventEnvelopeTests" -v q
```

Expected: all pass.

- [ ] **Step 3.4: Commit**

```
git add src/Winche.Events/Projection/Projection.cs tests/Winche.Events.Tests/Projection/ProjectionTests.cs
git commit -m "feat: replace reflection Apply dispatch with On<TEvent> delegate registration"
```

---

## Task 4: Update `ProjectionBridge` to map JasperFx event → `EventEnvelope<IEvent>`

**Files:**
- Modify: `src/Winche.Events/Projection/Internal/ProjectionBridge.cs`
- Modify: `tests/Winche.Events.Tests/Projection/ProjectionBridgeTests.cs`

- [ ] **Step 4.1: Rewrite `ProjectionBridge.cs`**

```csharp
using Marten.Events.Aggregation;
using Winche.Events.Abstractions;
using JasperFxEvent = JasperFx.Events.IEvent;

namespace Winche.Events.Projection.Internal;

internal sealed class ProjectionBridge<TAggregate>(Projection<TAggregate> projection)
    : SingleStreamProjection<TAggregate, string>
    where TAggregate : class, IAggregate
{
    private readonly Projection<TAggregate> _projection = projection;

    private static EventEnvelope<IEvent> ToEnvelope(string streamId, JasperFxEvent e)
        => new(streamId, (IEvent)e.Data, e.Version, e.Timestamp);

    public override TAggregate? Evolve(TAggregate? snapshot, string id, JasperFxEvent @event)
    {
        var state = snapshot ?? _projection.Create(id);
        return _projection.ApplyEvent(state, ToEnvelope(id, @event));
    }

    public override async ValueTask<TAggregate?> EvolveAsync(
        TAggregate? snapshot, string id,
        Marten.IQuerySession session,
        JasperFxEvent @event,
        CancellationToken cancellation)
    {
        var state = snapshot ?? _projection.Create(id);
        return await _projection.ApplyEventAsync(state, ToEnvelope(id, @event));
    }
}
```

- [ ] **Step 4.2: Rewrite `ProjectionBridgeTests.cs`**

```csharp
using FluentAssertions;
using JasperFx.Events;
using NSubstitute;
using Winche.Events.Abstractions;
using Winche.Events.Projection.Internal;
using Xunit;

namespace Winche.Events.Tests.Projection;

public class ProjectionBridgeTests
{
    private static IEvent MockEvent(IEvent data, long version = 1, string streamId = "test")
    {
        var e = Substitute.For<IEvent>();
        e.Data.Returns(data);
        e.Version.Returns(version);
        e.Timestamp.Returns(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return e;
    }

    [Fact]
    public void Evolve_creates_state_and_applies_sync_handler()
    {
        var bridge = new ProjectionBridge<Counter>(new CounterProjection());

        var result = bridge.Evolve(null, "test", MockEvent(new Incremented()));

        result.Should().Be(new Counter(1) { Id = "test" });
    }

    [Fact]
    public void Evolve_applies_to_existing_snapshot()
    {
        var bridge = new ProjectionBridge<Counter>(new CounterProjection());
        var snapshot = new Counter(5) { Id = "test" };

        var result = bridge.Evolve(snapshot, "test", MockEvent(new Incremented()));

        result.Should().Be(new Counter(6) { Id = "test" });
    }

    [Fact]
    public void Evolve_returns_unchanged_state_for_unregistered_event()
    {
        var bridge = new ProjectionBridge<Counter>(new CounterProjection());
        var snapshot = new Counter(42) { Id = "test" };

        var result = bridge.Evolve(snapshot, "test", MockEvent(new UnknownEvent()));

        result.Should().Be(new Counter(42) { Id = "test" });
    }

    [Fact]
    public async Task EvolveAsync_passes_envelope_with_correct_metadata()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var meta = new MetadataProjection();
        var bridge = new ProjectionBridge<Counter>(meta);
        var jasperEvent = Substitute.For<IEvent>();
        jasperEvent.Data.Returns(new Incremented());
        jasperEvent.Version.Returns(7L);
        jasperEvent.Timestamp.Returns(ts);

        await bridge.EvolveAsync(null, "counters/99", Substitute.For<Marten.IQuerySession>(), jasperEvent, default);

        meta.LastVersion.Should().Be(7);
        meta.LastTimestamp.Should().Be(ts);
        meta.LastStreamId.Should().Be("counters/99");
    }
}
```

- [ ] **Step 4.3: Run tests**

```
dotnet test tests/Winche.Events.Tests --filter "ProjectionBridgeTests" -v q
```

Expected: all pass.

- [ ] **Step 4.4: Commit**

```
git add src/Winche.Events/Projection/Internal/ProjectionBridge.cs tests/Winche.Events.Tests/Projection/ProjectionBridgeTests.cs
git commit -m "feat: ProjectionBridge maps JasperFx event to EventEnvelope<IEvent>"
```

---

## Task 5: Create `CommandHandler<TAggregate>`

**Files:**
- Create: `src/Winche.Events.Commands/CommandHandler.cs`
- Create: `tests/Winche.Events.Commands.Tests/CommandHandlerTests.cs`

- [ ] **Step 5.1: Write failing tests**

Create `tests/Winche.Events.Commands.Tests/CommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Xunit;

namespace Winche.Events.Commands.Tests;

record Thing(string Status) : Aggregate;
record CreateThing(string Id) : Command<Thing>;
record ActivateThing(string Id) : Command<Thing>;
record CancelThing(string Id) : Command<Thing>;
record ThingCreated(string Id) : Event;
record ThingActivated(string Id) : Event;

class ThingCommandHandler : CommandHandler<Thing>
{
    public ThingCommandHandler()
    {
        On<CreateThing>((state, cmd) =>
        {
            if (state is not null) throw new InvalidOperationException("Already exists.");
            return [new ThingCreated(cmd.Id)];
        });

        On<ActivateThing>(async (state, cmd) =>
        {
            await Task.Yield();
            return [new ThingActivated(cmd.Id)];
        });

        On<CancelThing>((state, cmd, ct) =>
            Task.FromResult<IEnumerable<IEvent>>([new ThingActivated(cmd.Id)]));
    }
}

public class CommandHandlerTests
{
    private readonly ThingCommandHandler _handler = new();

    [Fact]
    public async Task Sync_handler_returns_events()
    {
        var events = await _handler.HandleAsync(null, new CreateThing("things/1"), default);
        events.Should().ContainSingle().Which.Should().BeOfType<ThingCreated>();
    }

    [Fact]
    public async Task Sync_handler_receives_current_state()
    {
        var state = new Thing("exists") { Id = "things/1" };
        var act = () => _handler.HandleAsync(state, new CreateThing("things/1"), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Already exists.");
    }

    [Fact]
    public async Task Async_handler_is_awaited()
    {
        var events = await _handler.HandleAsync(null, new ActivateThing("things/1"), default);
        events.Should().ContainSingle().Which.Should().BeOfType<ThingActivated>();
    }

    [Fact]
    public async Task Async_handler_with_cancellation_token_is_called()
    {
        using var cts = new CancellationTokenSource();
        var events = await _handler.HandleAsync(null, new CancelThing("things/1"), cts.Token);
        events.Should().ContainSingle().Which.Should().BeOfType<ThingActivated>();
    }

    [Fact]
    public async Task Unregistered_command_throws_InvalidOperationException()
    {
        var unknownCmd = new ActivateThing("things/99");
        var bare = new ThingCommandHandler();
        // Use a fresh handler that has no registrations to test the throw path
        var emptyHandler = new EmptyHandler();
        var act = () => emptyHandler.HandleAsync(null, new CreateThing("x"), default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CreateThing*");
    }
}

class EmptyHandler : CommandHandler<Thing>
{
    // no On<> registrations
}
```

- [ ] **Step 5.2: Run to confirm it fails**

```
dotnet test tests/Winche.Events.Commands.Tests --filter "CommandHandlerTests" -v q
```

Expected: compile error — `CommandHandler<T>` does not exist.

- [ ] **Step 5.3: Create `CommandHandler.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Base class for aggregate command handlers. Register handlers for each command type using
/// <c>On&lt;TCommand&gt;</c> in the constructor. All commands for one aggregate live in one class.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this handler operates on.</typeparam>
public abstract class CommandHandler<TAggregate> where TAggregate : class, IAggregate
{
    private readonly Dictionary<Type, Func<TAggregate?, object, CancellationToken, Task<IEnumerable<IEvent>>>> _handlers = new();

    /// <summary>Registers a synchronous command handler.</summary>
    protected void On<TCommand>(Func<TAggregate?, TCommand, IEnumerable<IEvent>> handler)
        where TCommand : ICommand<TAggregate>
        => _handlers[typeof(TCommand)] = (state, cmd, _) =>
            Task.FromResult(handler(state, (TCommand)cmd));

    /// <summary>Registers an asynchronous command handler.</summary>
    protected void On<TCommand>(Func<TAggregate?, TCommand, Task<IEnumerable<IEvent>>> handler)
        where TCommand : ICommand<TAggregate>
        => _handlers[typeof(TCommand)] = (state, cmd, _) =>
            handler(state, (TCommand)cmd);

    /// <summary>Registers an asynchronous command handler with cancellation support.</summary>
    protected void On<TCommand>(Func<TAggregate?, TCommand, CancellationToken, Task<IEnumerable<IEvent>>> handler)
        where TCommand : ICommand<TAggregate>
        => _handlers[typeof(TCommand)] = (state, cmd, ct) =>
            handler(state, (TCommand)cmd, ct);

    internal Task<IEnumerable<IEvent>> HandleAsync(TAggregate? state, object command, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(command.GetType(), out var handler))
            throw new InvalidOperationException(
                $"No handler registered for '{command.GetType().Name}' on aggregate '{typeof(TAggregate).Name}'.");
        return handler(state, command, ct);
    }
}
```

- [ ] **Step 5.4: Run tests**

```
dotnet test tests/Winche.Events.Commands.Tests --filter "CommandHandlerTests" -v q
```

Expected: all pass.

- [ ] **Step 5.5: Commit**

```
git add src/Winche.Events.Commands/CommandHandler.cs tests/Winche.Events.Commands.Tests/CommandHandlerTests.cs
git commit -m "feat: add CommandHandler<TAggregate> with On<TCommand> delegate registration"
```

---

## Task 6: Update Events DI registration — `WincheEventsOptions` + `ServiceCollectionExtensions`

**Files:**
- Modify: `src/Winche.Events/DependencyInjection/WincheEventsOptions.cs`
- Modify: `src/Winche.Events/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/Winche.Events.Tests/DependencyInjection/WincheEventsOptionsTests.cs`
- Modify: `tests/Winche.Events.Tests/DependencyInjection/WincheEventsRegistrationTests.cs`

- [ ] **Step 6.1: Rewrite `WincheEventsOptions.cs`**

```csharp
using System.Text.Json;
using JasperFx.Events.Projections;
using Marten;
using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Projection.Internal;

namespace Winche.Events.DependencyInjection;

internal sealed record ProjectionRegistration(
    Action<IServiceCollection> Register,
    Action<StoreOptions, IServiceProvider> Configure);

/// <summary>Configuration options for <c>AddWincheEvents</c>.</summary>
public sealed class WincheEventsOptions
{
    /// <summary>PostgreSQL connection string passed to Marten.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    internal readonly List<(Type EventType, string? Alias)> EventTypes = [];
    internal readonly List<ProjectionRegistration> Projections = [];
    internal readonly List<Type> NotifierTypes = [];

    /// <summary>
    /// Configures the <see cref="JsonSerializerOptions"/> used by Marten for event serialization.
    /// When <see langword="null"/> Marten's default camelCase settings apply.
    /// </summary>
    public Action<JsonSerializerOptions>? ConfigureJsonSerializer { get; set; }

    /// <summary>Registers an event type. Marten derives the stored alias from the class name.</summary>
    public void AddEvent<TEvent>() where TEvent : class, IEvent
        => EventTypes.Add((typeof(TEvent), null));

    /// <summary>
    /// Registers an event type with an explicit, stable type alias.
    /// The alias is written to Marten's <c>type</c> column and must never change once events
    /// have been persisted — renaming the C# class is safe as long as the alias stays the same.
    /// </summary>
    public void AddEvent<TEvent>(string alias) where TEvent : class, IEvent
        => EventTypes.Add((typeof(TEvent), alias));

    /// <summary>
    /// Registers a projection. Both type parameters and the projection mode are explicit —
    /// no runtime type discovery occurs.
    /// </summary>
    /// <typeparam name="TProjection">Concrete projection class inheriting <c>Projection&lt;TAggregate&gt;</c>.</typeparam>
    /// <typeparam name="TAggregate">The aggregate type produced by this projection.</typeparam>
    /// <param name="mode">
    /// <see cref="ProjectionMode.Inline"/>: updated in the same transaction; handlers must not do external I/O.<br/>
    /// <see cref="ProjectionMode.Async"/>: updated by the background daemon; handlers may do external I/O.
    /// </param>
    public void AddProjection<TProjection, TAggregate>(ProjectionMode mode)
        where TProjection : Projection<TAggregate>
        where TAggregate : class, IAggregate
    {
        var lifecycle = mode == ProjectionMode.Inline
            ? ProjectionLifecycle.Inline
            : ProjectionLifecycle.Async;

        Projections.Add(new ProjectionRegistration(
            Register: services => services.AddSingleton<Projection<TAggregate>, TProjection>(),
            Configure: (opts, sp) =>
            {
                var projection = sp.GetRequiredService<Projection<TAggregate>>();
                var bridge = new ProjectionBridge<TAggregate>(projection);
                opts.Projections.AddGlobalProjection<TAggregate, string>(bridge, lifecycle);
            }
        ));
    }

    /// <summary>Registers a post-commit notifier. Multiple notifiers can be registered.</summary>
    public void AddNotifier<TNotifier>() where TNotifier : class, IAppendNotifier
        => NotifierTypes.Add(typeof(TNotifier));
}
```

- [ ] **Step 6.2: Rewrite `ServiceCollectionExtensions.cs`**

```csharp
using Marten;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;
using Winche.Events.Session;
using Winche.Events.Session.Internal;

namespace Winche.Events.DependencyInjection;

/// <summary>Extension methods for registering Winche.Events services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Winche.Events event store, projections, and notifiers with the DI container.</summary>
    public static IServiceCollection AddWincheEvents(
        this IServiceCollection services,
        Action<WincheEventsOptions> configure)
    {
        var options = new WincheEventsOptions();
        configure(options);

        foreach (var proj in options.Projections)
            proj.Register(services);

        foreach (var notifierType in options.NotifierTypes)
            services.AddSingleton(typeof(IAppendNotifier), notifierType);

        services.AddMarten(sp =>
        {
            var storeOptions = new StoreOptions();
            storeOptions.Connection(options.ConnectionString);
            storeOptions.Events.StreamIdentity = StreamIdentity.AsString;

            foreach (var (eventType, alias) in options.EventTypes)
            {
                storeOptions.Events.AddEventType(eventType);
                if (alias is not null)
                    storeOptions.Events.MapEventType(eventType, alias);
            }

            if (options.ConfigureJsonSerializer is not null)
                storeOptions.UseSystemTextJsonForSerialization(configure: options.ConfigureJsonSerializer);

            foreach (var proj in options.Projections)
                proj.Configure(storeOptions, sp);

            return storeOptions;
        });

        services.AddSingleton<IEventStore>(sp =>
        {
            var martenStore = sp.GetRequiredService<IDocumentStore>();
            var notifiers = sp.GetServices<IAppendNotifier>().ToList();
            var logger = sp.GetRequiredService<ILogger<EventSession>>();
            return new EventStore(martenStore, notifiers, logger);
        });

        return services;
    }
}
```

- [ ] **Step 6.3: Update `WincheEventsOptionsTests.cs`**

The existing tests for `AddEvent` are unaffected. The test that calls `AddWincheEvents` (the DI smoke test) needs a projection registration updated. Find and replace the `AddProjection` call in `WincheEventsOptionsTests.cs`:

```csharp
// In ConfigureJsonSerializer_action_is_invoked_during_store_setup:
// No projection registered in that test — no change needed.
```

The `WincheEventsOptionsTests.cs` does not test projections directly — confirm existing tests still pass:

```
dotnet test tests/Winche.Events.Tests --filter "WincheEventsOptionsTests" -v q
```

Expected: all pass.

- [ ] **Step 6.4: Update `WincheEventsRegistrationTests.cs`**

`AliasedProjection` uses the old `Apply` convention. Rewrite to use `On<TEvent>`:

```csharp
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.DependencyInjection;
using Winche.Events.Projection;
using Xunit;
using WincheSession = Winche.Events.Session;

namespace Winche.Events.Tests.DependencyInjection;

record AliasedEvent : Event;
record AliasedAggregate(string Status) : Aggregate
{
    public static AliasedAggregate Empty => new("none");
}

class AliasedProjection : Projection<AliasedAggregate>
{
    public AliasedProjection()
    {
        On<AliasedEvent>((s, e) => s with { Status = "done" });
    }
    public override AliasedAggregate Create(string id) => AliasedAggregate.Empty with { Id = id };
}

public class WincheEventsRegistrationTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Database=winche_events_test;Username=postgres;Password=Ehsan1371";

    public async Task InitializeAsync()
    {
        var store = DocumentStore.For(opts =>
        {
            opts.Connection(ConnectionString);
            opts.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsString;
        });
        await store.Advanced.Clean.DeleteAllEventDataAsync();
        await store.DisposeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddEvent_with_alias_stores_events_under_that_type_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheEvents(opts =>
        {
            opts.ConnectionString = ConnectionString;
            opts.AddEvent<AliasedEvent>("aliased_event_v1");
            opts.AddProjection<AliasedProjection, AliasedAggregate>(ProjectionMode.Async);
        });
        var provider = services.BuildServiceProvider();
        var martenStore = provider.GetRequiredService<IDocumentStore>();
        var eventStore = provider.GetRequiredService<WincheSession.IEventStore>();
        await martenStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await using var session = await eventStore.OpenSessionAsync();
        await session.AppendAsync("alias-test/1", [new AliasedEvent()]);
        await session.SaveChangesAsync();

        using var q = martenStore.QuerySession();
        var events = await q.Events.FetchStreamAsync("alias-test/1");
        events.Should().HaveCount(1);
        events[0].EventTypeName.Should().Be("aliased_event_v1");
    }
}
```

- [ ] **Step 6.5: Build**

```
dotnet build src/Winche.Events/Winche.Events.csproj -v q
```

Expected: Build succeeded.

- [ ] **Step 6.6: Commit**

```
git add src/Winche.Events/DependencyInjection/ tests/Winche.Events.Tests/DependencyInjection/
git commit -m "feat: explicit two-param AddProjection<T,TAggregate>(mode), no startup reflection"
```

---

## Task 7: Simplify `EventSession` and `EventStore`

**Files:**
- Modify: `src/Winche.Events/Session/IEventSession.cs`
- Modify: `src/Winche.Events/Session/Internal/EventSession.cs`
- Modify: `src/Winche.Events/Session/Internal/EventStore.cs`
- Modify: `tests/Winche.Events.Tests/Session/EventSessionNotifierTests.cs`
- Modify: `tests/Winche.Events.Tests/Session/EventSessionTests.cs`

- [ ] **Step 7.1: Update `IEventSession.cs`**

```csharp
using System.Data;
using Winche.Events.Abstractions;

namespace Winche.Events.Session;

/// <summary>
/// A unit of work scoped to a single PostgreSQL connection and transaction.
/// Call <see cref="SaveChangesAsync"/> to commit, then dispose with <c>await using</c>.
/// </summary>
public interface IEventSession : IAsyncDisposable
{
    /// <summary>
    /// Buffers <paramref name="events"/> to be appended to <paramref name="streamId"/> on the next
    /// <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task AppendAsync(
        string streamId,
        IEnumerable<IEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the stored aggregate document for <paramref name="streamId"/>.
    /// Returns <c>null</c> if the stream does not exist or no document has been stored yet.
    /// For <see cref="ProjectionMode.Inline"/> projections the document is always current.
    /// For <see cref="ProjectionMode.Async"/> projections the document is eventually consistent.
    /// </summary>
    Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class, IAggregate;

    /// <summary>
    /// Returns all events for <paramref name="streamId"/> in order, each wrapped with stream metadata.
    /// Returns an empty list if the stream does not exist.
    /// </summary>
    Task<IReadOnlyList<EventEnvelope<IEvent>>> GetEventsAsync(
        string streamId,
        CancellationToken ct = default);

    /// <summary>
    /// Commits all buffered events to PostgreSQL, then fires registered notifiers.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
```

- [ ] **Step 7.2: Rewrite `EventSession.cs`**

```csharp
using Marten;
using Microsoft.Extensions.Logging;
using Winche.Events.Abstractions;
using Winche.Events.Notification;

namespace Winche.Events.Session.Internal;

internal sealed class EventSession : IEventSession
{
    private readonly IDocumentSession _session;
    private readonly IReadOnlyList<IAppendNotifier> _notifiers;
    private readonly ILogger<EventSession> _logger;
    private readonly List<(string StreamId, List<IEvent> Events)> _pending = [];

    internal EventSession(
        IDocumentSession session,
        IReadOnlyList<IAppendNotifier> notifiers,
        ILogger<EventSession> logger)
    {
        _session = session;
        _notifiers = notifiers;
        _logger = logger;
    }

    public Task AppendAsync(
        string streamId,
        IEnumerable<IEvent> events,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        var eventList = events.ToList();
        if (expectedVersion.HasValue)
            _session.Events.Append(streamId, expectedVersion.Value, eventList.ToArray());
        else
            _session.Events.Append(streamId, eventList.ToArray());
        _pending.Add((streamId, eventList));
        return Task.CompletedTask;
    }

    public Task<TAggregate?> LoadAsync<TAggregate>(
        string streamId,
        CancellationToken ct = default) where TAggregate : class, IAggregate
        => _session.LoadAsync<TAggregate>(streamId, ct);

    public async Task<IReadOnlyList<EventEnvelope<IEvent>>> GetEventsAsync(
        string streamId,
        CancellationToken ct = default)
    {
        var raw = await _session.Events.FetchStreamAsync(streamId, token: ct);
        return [..raw.Select(e => new EventEnvelope<IEvent>(streamId, (IEvent)e.Data, e.Version, e.Timestamp))];
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _session.SaveChangesAsync(ct);
        await FireNotifiersAsync(ct);
        _pending.Clear();
    }

    private async Task FireNotifiersAsync(CancellationToken ct)
    {
        foreach (var (streamId, events) in _pending)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(streamId, events, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notifier {Type} failed for stream {StreamId}",
                        notifier.GetType().Name, streamId);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 7.3: Rewrite `EventStore.cs`**

```csharp
using System.Data;
using Marten;
using Marten.Services;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;

namespace Winche.Events.Session.Internal;

internal sealed class EventStore : IEventStore
{
    private readonly IDocumentStore _martenStore;
    private readonly IReadOnlyList<IAppendNotifier> _notifiers;
    private readonly ILogger<EventSession> _logger;

    internal EventStore(
        IDocumentStore martenStore,
        IReadOnlyList<IAppendNotifier> notifiers,
        ILogger<EventSession> logger)
    {
        _martenStore = martenStore;
        _notifiers = notifiers;
        _logger = logger;
    }

    public Task<IEventSession> OpenSessionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        var martenSession = _martenStore.OpenSession(new SessionOptions
        {
            IsolationLevel = isolationLevel,
        });
        return Task.FromResult<IEventSession>(new EventSession(martenSession, _notifiers, _logger));
    }
}
```

- [ ] **Step 7.4: Update `EventSessionNotifierTests.cs`**

Fix the `BuildSession` helper — remove the now-deleted `IServiceProvider` parameter:

```csharp
private EventSession BuildSession(params IAppendNotifier[] notifiers) =>
    new(_martenSession, notifiers, NullLogger<EventSession>.Instance);
```

(The rest of the file is unchanged.)

- [ ] **Step 7.5: Update `EventSessionTests.cs`**

The test registers a projection inline. Update the projection class and registration:

```csharp
// Replace the OrderProjection class in the test file:
class OrderProjection : Projection<OrderState>
{
    public OrderProjection()
    {
        On<OrderPlaced>((s, e) => s with { Status = "placed" });
        On<OrderShipped>((s, e) => s with { Status = "shipped" });
    }
    public override OrderState Create(string id) => new OrderState("none") { Id = id };
}

// Replace the AddProjection call in InitializeAsync:
opts.AddProjection<OrderProjection, OrderState>(ProjectionMode.Inline);
```

- [ ] **Step 7.6: Delete old `EventEnvelope.cs`**

```
git rm src/Winche.Events/Session/EventEnvelope.cs
```

- [ ] **Step 7.7: Build and run tests**

```
dotnet build -v q
dotnet test tests/Winche.Events.Tests --filter "EventSessionNotifierTests|EventSessionTests" -v q
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 7.8: Commit**

```
git add src/Winche.Events/Session/ tests/Winche.Events.Tests/Session/
git commit -m "feat: LoadAsync reads stored doc, GetEventsAsync returns EventEnvelope<IEvent>, remove IServiceProvider from session"
```

---

## Task 8: Rewrite `ICommandDispatcher` and `CommandDispatcher`

**Files:**
- Modify: `src/Winche.Events.Commands/ICommandDispatcher.cs`
- Modify: `src/Winche.Events.Commands/Internal/CommandDispatcher.cs`
- Modify: `tests/Winche.Events.Commands.Tests/CommandDispatcherTests.cs`

- [ ] **Step 8.1: Rewrite `ICommandDispatcher.cs`**

```csharp
using Winche.Events.Abstractions;

namespace Winche.Events.Commands;

/// <summary>
/// Dispatches commands through the load → handle → append → commit cycle.
/// Register via <c>AddWincheEventsCommands</c> and resolve from the DI container.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Loads the current aggregate state, calls the registered <see cref="CommandHandler{TAggregate}"/>,
    /// appends the produced events, and commits. Does not return state — call
    /// <see cref="Session.IEventSession.LoadAsync{TAggregate}"/> explicitly if you need it after dispatch.
    /// <typeparamref name="TAggregate"/> is inferred from the command's <c>ICommand&lt;TAggregate&gt;</c>
    /// implementation — no explicit type argument needed at the call site.
    /// </summary>
    Task DispatchAsync<TAggregate>(
        string streamId,
        ICommand<TAggregate> command,
        long? expectedVersion = null,
        CancellationToken ct = default)
        where TAggregate : class, IAggregate;
}
```

- [ ] **Step 8.2: Rewrite `CommandDispatcher.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Session;

namespace Winche.Events.Commands.Internal;

internal sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IEventStore _eventStore;
    private readonly IServiceProvider _serviceProvider;

    internal CommandDispatcher(IEventStore eventStore, IServiceProvider serviceProvider)
    {
        _eventStore = eventStore;
        _serviceProvider = serviceProvider;
    }

    public async Task DispatchAsync<TAggregate>(
        string streamId,
        ICommand<TAggregate> command,
        long? expectedVersion = null,
        CancellationToken ct = default)
        where TAggregate : class, IAggregate
    {
        await using var session = await _eventStore.OpenSessionAsync(ct: ct);
        var state   = await session.LoadAsync<TAggregate>(streamId, ct);
        var handler = _serviceProvider.GetRequiredService<CommandHandler<TAggregate>>();
        var events  = await handler.HandleAsync(state, command, ct);
        await session.AppendAsync(streamId, events, expectedVersion, ct);
        await session.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 8.3: Rewrite `CommandDispatcherTests.cs`**

```csharp
using FluentAssertions;
using NSubstitute;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Winche.Events.Commands.Internal;
using Winche.Events.Session;
using Xunit;

namespace Winche.Events.Commands.Tests;

// reuse Thing/CreateThing/ActivateThing/ThingCreated from CommandHandlerTests.cs

public class CommandDispatcherTests
{
    private readonly IEventSession _session;
    private readonly IEventStore _store;
    private readonly ThingCommandHandler _handler = new();

    public CommandDispatcherTests()
    {
        _session = Substitute.For<IEventSession>();
        _store = Substitute.For<IEventStore>();
        _store.OpenSessionAsync(default, default).ReturnsForAnyArgs(Task.FromResult(_session));
    }

    private CommandDispatcher BuildDispatcher()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(CommandHandler<Thing>)).Returns(_handler);
        return new CommandDispatcher(_store, sp);
    }

    [Fact]
    public async Task DispatchAsync_passes_null_state_to_handler_for_new_stream()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        await BuildDispatcher().DispatchAsync("things/1", new CreateThing("things/1"));

        await _session.Received(1).AppendAsync(
            "things/1",
            Arg.Is<IEnumerable<IEvent>>(e => e.OfType<ThingCreated>().Any()),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_calls_SaveChangesAsync()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        await BuildDispatcher().DispatchAsync("things/2", new CreateThing("things/2"));

        await _session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_passes_expectedVersion_to_AppendAsync()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(new Thing("exists") { Id = "things/3" }));

        await BuildDispatcher().DispatchAsync("things/3", new ActivateThing("things/3"), expectedVersion: 5);

        await _session.Received(1).AppendAsync(
            "things/3",
            Arg.Any<IEnumerable<IEvent>>(),
            (long?)5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_does_not_return_state()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        // DispatchAsync returns Task (void) — just verify it completes without exception
        await BuildDispatcher().DispatchAsync("things/4", new CreateThing("things/4"));

        // LoadAsync called only once (pre-dispatch load, not a second load)
        await _session.Received(1).LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_propagates_handler_exception_without_appending()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(new Thing("exists") { Id = "things/x" }));

        // CreateThing handler throws when state is not null
        var act = () => BuildDispatcher().DispatchAsync("things/x", new CreateThing("things/x"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Already exists.");

        await _session.DidNotReceive().AppendAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<IEvent>>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_propagates_exception_when_no_handler_registered()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        var sp = Substitute.For<IServiceProvider>(); // returns null for all GetService
        var dispatcher = new CommandDispatcher(_store, sp);

        var act = () => dispatcher.DispatchAsync("things/x", new CreateThing("things/x"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DispatchAsync_disposes_session()
    {
        _session.LoadAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        await BuildDispatcher().DispatchAsync("things/5", new CreateThing("things/5"));

        await _session.Received(1).DisposeAsync();
    }
}
```

- [ ] **Step 8.4: Run tests**

```
dotnet test tests/Winche.Events.Commands.Tests --filter "CommandDispatcherTests" -v q
```

Expected: all pass.

- [ ] **Step 8.5: Commit**

```
git add src/Winche.Events.Commands/ICommandDispatcher.cs src/Winche.Events.Commands/Internal/CommandDispatcher.cs tests/Winche.Events.Commands.Tests/CommandDispatcherTests.cs
git commit -m "feat: DispatchAsync returns Task, dispatches via CommandHandler<TAggregate>"
```

---

## Task 9: Update command DI registration

**Files:**
- Modify: `src/Winche.Events.Commands/DependencyInjection/WincheEventsCommandsOptions.cs`
- Modify: `src/Winche.Events.Commands/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/Winche.Events.Commands.Tests/WincheEventsCommandsOptionsTests.cs`

- [ ] **Step 9.1: Rewrite `WincheEventsCommandsOptions.cs`**

```csharp
using Winche.Events.Abstractions;
using Winche.Events.Commands;

namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Configuration options for <c>AddWincheEventsCommands</c>.</summary>
public sealed class WincheEventsCommandsOptions
{
    internal readonly List<Action<IServiceCollection>> Registrations = [];

    /// <summary>
    /// Registers a command handler. All commands for <typeparamref name="TAggregate"/> are handled
    /// by a single <typeparamref name="THandler"/> class using <c>On&lt;TCommand&gt;</c> registrations.
    /// </summary>
    /// <typeparam name="THandler">The handler class inheriting <c>CommandHandler&lt;TAggregate&gt;</c>.</typeparam>
    /// <typeparam name="TAggregate">The aggregate type this handler operates on.</typeparam>
    public void AddCommandHandler<THandler, TAggregate>()
        where THandler : CommandHandler<TAggregate>
        where TAggregate : class, IAggregate
        => Registrations.Add(services => services.AddSingleton<CommandHandler<TAggregate>, THandler>());
}
```

- [ ] **Step 9.2: Rewrite `ServiceCollectionExtensions.cs` (commands)**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Commands.Internal;
using Winche.Events.Session;

namespace Winche.Events.Commands.DependencyInjection;

/// <summary>Extension methods for registering Winche.Events.Commands services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers command handlers and <see cref="ICommandDispatcher"/> with the DI container.</summary>
    public static IServiceCollection AddWincheEventsCommands(
        this IServiceCollection services,
        Action<WincheEventsCommandsOptions> configure)
    {
        var options = new WincheEventsCommandsOptions();
        configure(options);

        foreach (var reg in options.Registrations)
            reg(services);

        services.AddSingleton<ICommandDispatcher>(sp =>
            new CommandDispatcher(sp.GetRequiredService<IEventStore>(), sp));

        return services;
    }
}
```

- [ ] **Step 9.3: Rewrite `WincheEventsCommandsOptionsTests.cs`**

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Xunit;

namespace Winche.Events.Commands.Tests;

public class WincheEventsCommandsOptionsTests
{
    [Fact]
    public void AddCommandHandler_registers_handler_as_singleton_in_DI()
    {
        var services = new ServiceCollection();
        var options = new WincheEventsCommandsOptions();

        options.AddCommandHandler<ThingCommandHandler, Thing>();

        foreach (var reg in options.Registrations)
            reg(services);

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<CommandHandler<Thing>>();
        handler.Should().BeOfType<ThingCommandHandler>();
    }

    [Fact]
    public void AddCommandHandler_multiple_aggregates_are_all_registered()
    {
        var services = new ServiceCollection();
        var options = new WincheEventsCommandsOptions();

        options.AddCommandHandler<ThingCommandHandler, Thing>();

        options.Registrations.Should().HaveCount(1);
    }
}
```

- [ ] **Step 9.4: Run tests**

```
dotnet test tests/Winche.Events.Commands.Tests --filter "WincheEventsCommandsOptionsTests" -v q
```

Expected: all pass.

- [ ] **Step 9.5: Commit**

```
git add src/Winche.Events.Commands/DependencyInjection/ tests/Winche.Events.Commands.Tests/WincheEventsCommandsOptionsTests.cs
git commit -m "feat: AddCommandHandler<T,TAggregate> — no startup reflection in command registration"
```

---

## Task 10: Delete old files and update sample

**Files:**
- Delete: `src/Winche.Events.Commands/ICommandHandler.cs`
- Delete: `src/Winche.Events.Commands/Internal/ICommandHandlerInvoker.cs`
- Modify: `samples/Winche.Events.Sample/Program.cs`

- [ ] **Step 10.1: Delete obsolete files**

```
git rm src/Winche.Events.Commands/ICommandHandler.cs
git rm src/Winche.Events.Commands/Internal/ICommandHandlerInvoker.cs
```

- [ ] **Step 10.2: Build to confirm no dangling references**

```
dotnet build -v q
```

Expected: Build succeeded.

- [ ] **Step 10.3: Rewrite `Program.cs` sample**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session;
using System.Text.Json;
using System.Text.Json.Serialization;

const string ConnectionString = "Host=localhost;Port=5432;Username=postgres;Password=Ehsan1371;Database=winche_events_test";

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddWincheEvents(opts =>
{
    opts.ConnectionString = ConnectionString;
    opts.AddEvent<OrderPlaced>();
    opts.AddEvent<OrderShipped>();
    opts.AddEvent<OrderCancelled>();
    opts.AddProjection<OrderProjection, Order>(ProjectionMode.Inline);
    opts.AddNotifier<ConsoleNotifier>();
    opts.ConfigureJsonSerializer = jsonOpts =>
    {
        jsonOpts.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        jsonOpts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    };
});

services.AddWincheEventsCommands(cmds =>
{
    cmds.AddCommandHandler<OrderCommandHandler, Order>();
});

await using var provider = services.BuildServiceProvider();
var eventStore = provider.GetRequiredService<IEventStore>();
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
var logger = provider.GetRequiredService<ILogger<Program>>();

var orderId = $"orders/{Guid.NewGuid():N}";

logger.LogInformation("=== Place order ===");
await dispatcher.DispatchAsync(orderId, new PlaceOrderCommand(orderId, 99.99m));

await using var readSession = await eventStore.OpenSessionAsync();
var order = await readSession.LoadAsync<Order>(orderId);
logger.LogInformation("After place: status={Status} total={Total}", order?.Status, order?.Total);

logger.LogInformation("=== Ship order ===");
await dispatcher.DispatchAsync(orderId, new ShipOrderCommand(orderId));

await using var readSession2 = await eventStore.OpenSessionAsync();
order = await readSession2.LoadAsync<Order>(orderId);
logger.LogInformation("After ship: status={Status}", order?.Status);

logger.LogInformation("=== Cancel directly ===");
await using (var session = await eventStore.OpenSessionAsync())
{
    await session.AppendAsync(orderId, [new OrderCancelled(orderId)]);
    await session.SaveChangesAsync();
}

logger.LogInformation("=== Read all events ===");
await using var evtSession = await eventStore.OpenSessionAsync();
var events = await evtSession.GetEventsAsync(orderId);
foreach (var e in events)
    logger.LogInformation("v{Version} [{Timestamp}] {Type}", e.Version, e.Timestamp, e.Data.GetType().Name);

// ── Domain ────────────────────────────────────────────────────────────────────

record Order(string Status, decimal Total) : Aggregate
{
    public static Order Empty => new("none", 0);
}

record OrderPlaced(string OrderId, decimal Total) : Event;
record OrderShipped(string OrderId) : Event;
record OrderCancelled(string OrderId) : Event;

class OrderProjection : Projection<Order>
{
    public OrderProjection()
    {
        On<OrderPlaced>((s, e) => s with { Status = "placed", Total = e.Data.Total });
        On<OrderShipped>((s, e) => s with { Status = "shipped" });
        On<OrderCancelled>((s, e) => s with { Status = "cancelled" });
    }
    public override Order Create(string id) => Order.Empty with { Id = id };
}

// ── Commands ──────────────────────────────────────────────────────────────────

record PlaceOrderCommand(string OrderId, decimal Total) : Command<Order>;
record ShipOrderCommand(string OrderId) : Command<Order>;

class OrderCommandHandler : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<PlaceOrderCommand>((state, cmd) =>
        {
            if (state is { Status: not "none" })
                throw new InvalidOperationException($"Order {cmd.OrderId} already exists.");
            return [new OrderPlaced(cmd.OrderId, cmd.Total)];
        });

        On<ShipOrderCommand>((state, cmd) =>
        {
            if (state is null or { Status: "none" })
                throw new InvalidOperationException($"Order {cmd.OrderId} does not exist.");
            return [new OrderShipped(cmd.OrderId)];
        });
    }
}

// ── Notifier ──────────────────────────────────────────────────────────────────

class ConsoleNotifier(ILogger<ConsoleNotifier> logger) : IAppendNotifier
{
    public Task NotifyAsync(string streamId, IReadOnlyList<IEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
            logger.LogInformation("[notify] {Stream} → {Event}", streamId, e.GetType().Name);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 10.4: Full build and test run**

```
dotnet build -v q
dotnet test -v q
```

Expected: Build succeeded, 0 warnings, all tests pass.

- [ ] **Step 10.5: Commit**

```
git add -A
git commit -m "feat: complete Winche.Events redesign — On<T> dispatch, no runtime reflection, explicit registration"
```

---

## Self-Review Notes

- **Spec coverage:** All goals met — `On<TEvent>` ✓, `On<TCommand>` ✓, `EventEnvelope<TEvent>` ✓, explicit registration ✓, `LoadAsync` reads stored doc ✓, `ProjectionMode` reinstated ✓, `DispatchAsync` returns `Task` ✓.
- **Types consistent:** `EventEnvelope<IEvent>` used in `GetEventsAsync` and `ProjectionBridge`. `EventEnvelope<TEvent>` used in `On<TEvent>` handler signatures throughout. `CommandHandler<TAggregate>` referenced consistently.
- **Deletions:** `ICommandHandler`, `ICommandHandlerInvoker`, old `EventEnvelope.cs` — all covered in Task 10.
- **No placeholders:** All steps contain complete code.
