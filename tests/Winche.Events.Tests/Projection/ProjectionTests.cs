using FluentAssertions;
using Winche.Events.Abstractions;
using Winche.Events.Projection;
using Xunit;

namespace Winche.Events.Tests.Projection;

public record Counter(int Value) : Aggregate;
public record Incremented;
public record Decremented;
public record UnknownEvent;

public class CounterProjection : Projection<Counter>
{
    public override Counter Create(string id) => new Counter(0) { Id = id };
    public Counter Apply(Counter state, Incremented _) => state with { Value = state.Value + 1 };
    public Counter Apply(Counter state, Decremented _) => state with { Value = state.Value - 1 };
}

public class SpyProjection : Projection<Counter>
{
    public bool FallbackCalled { get; private set; }
    public override Counter Create(string id) => new Counter(0) { Id = id };
    public Counter Apply(Counter state, Incremented _) => state with { Value = 1 };
    public Counter Apply(Counter state, object @event) { FallbackCalled = true; return state; }
}

class AsyncCounterProjection : Projection<Counter>
{
    public override Counter Create(string id) => new Counter(0) { Id = id };
    public Task<Counter> ApplyAsync(Counter state, Incremented _) => Task.FromResult(state with { Value = state.Value + 10 });
    public Task<Counter> ApplyAsync(Counter state, Decremented _) => Task.FromResult(state with { Value = state.Value - 1 });
    public Counter Apply(Counter state, Incremented _) => state with { Value = state.Value + 1 };
}

class TrulyAsyncProjection : Projection<Counter>
{
    public override Counter Create(string id) => new Counter(0) { Id = id };
    public async Task<Counter> ApplyAsync(Counter state, Incremented _)
    {
        await Task.Yield();
        return state with { Value = state.Value + 10 };
    }
}

public class ProjectionTests
{
    private readonly CounterProjection _projection = new();

    [Fact]
    public void InitialState_returns_zero_counter()
    {
        _projection.Create("test").Should().Be(new Counter(0) { Id = "test" });
    }

    [Fact]
    public void ApplyEvent_dispatches_to_typed_overload_for_known_event()
    {
        var result = _projection.ApplyEvent(new Counter(0) { Id = "test" }, new Incremented());
        result.Should().Be(new Counter(1) { Id = "test" });
    }

    [Fact]
    public void ApplyEvent_chains_multiple_events_in_order()
    {
        var state = _projection.Create("test");
        state = _projection.ApplyEvent(state, new Incremented());
        state = _projection.ApplyEvent(state, new Incremented());
        state = _projection.ApplyEvent(state, new Decremented());
        state.Should().Be(new Counter(1) { Id = "test" });
    }

    [Fact]
    public void ApplyEvent_returns_state_unchanged_for_unhandled_event_type()
    {
        var result = _projection.ApplyEvent(new Counter(42) { Id = "test" }, new UnknownEvent());
        result.Should().Be(new Counter(42) { Id = "test" });
    }

    [Fact]
    public void ApplyEvent_does_not_invoke_base_fallback_for_handled_event_type()
    {
        var spy = new SpyProjection();
        spy.ApplyEvent(spy.Create("test"), new Incremented());
        spy.FallbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyEventAsync_prefers_async_overload_over_sync_when_both_exist()
    {
        var projection = new AsyncCounterProjection();
        var result = await projection.ApplyEventAsync(new Counter(0) { Id = "test" }, new Incremented());
        result.Value.Should().Be(10); // async adds 10, sync adds 1 — async must win
    }

    [Fact]
    public async Task ApplyEventAsync_falls_back_to_sync_apply_when_no_async_overload_exists()
    {
        var result = await _projection.ApplyEventAsync(new Counter(5) { Id = "test" }, new Incremented());
        result.Should().Be(new Counter(6) { Id = "test" });
    }

    [Fact]
    public async Task ApplyEventAsync_chains_multiple_events_in_order()
    {
        var projection = new AsyncCounterProjection();
        var state = projection.Create("test");
        state = await projection.ApplyEventAsync(state, new Incremented());
        state = await projection.ApplyEventAsync(state, new Incremented());
        state = await projection.ApplyEventAsync(state, new Decremented());
        state.Value.Should().Be(19); // +10, +10, -1
    }

    [Fact]
    public async Task ApplyEventAsync_ignores_unhandled_event_types()
    {
        var projection = new AsyncCounterProjection();
        var result = await projection.ApplyEventAsync(new Counter(42) { Id = "test" }, new UnknownEvent());
        result.Should().Be(new Counter(42) { Id = "test" });
    }

    [Fact]
    public async Task ApplyEventAsync_awaits_genuine_async_state_machine()
    {
        var projection = new TrulyAsyncProjection();
        var state = projection.Create("test");
        state = await projection.ApplyEventAsync(state, new Incremented());
        state = await projection.ApplyEventAsync(state, new Incremented());
        state.Value.Should().Be(20);
    }
}
