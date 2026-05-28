using FluentAssertions;
using Winche.Events.Projection;
using Xunit;

namespace Winche.Events.Tests.Projection;

public record Counter(int Value);
public record Incremented;
public record Decremented;
public record UnknownEvent;

public class CounterProjection : Projection<Counter>
{
    public override Counter InitialState() => new Counter(0);
    public Counter Apply(Counter state, Incremented _) => state with { Value = state.Value + 1 };
    public Counter Apply(Counter state, Decremented _) => state with { Value = state.Value - 1 };
}

public class SpyProjection : Projection<Counter>
{
    public bool FallbackCalled { get; private set; }
    public override Counter InitialState() => new Counter(0);
    public Counter Apply(Counter state, Incremented _) => state with { Value = 1 };
    public Counter Apply(Counter state, object @event) { FallbackCalled = true; return state; }
}

public class ProjectionTests
{
    private readonly CounterProjection _projection = new();

    [Fact]
    public void InitialState_returns_zero_counter()
    {
        _projection.InitialState().Should().Be(new Counter(0));
    }

    [Fact]
    public void ApplyEvent_dispatches_to_typed_overload_for_known_event()
    {
        var result = _projection.ApplyEvent(new Counter(0), new Incremented());
        result.Should().Be(new Counter(1));
    }

    [Fact]
    public void ApplyEvent_chains_multiple_events_in_order()
    {
        var state = _projection.InitialState();
        state = _projection.ApplyEvent(state, new Incremented());
        state = _projection.ApplyEvent(state, new Incremented());
        state = _projection.ApplyEvent(state, new Decremented());
        state.Should().Be(new Counter(1));
    }

    [Fact]
    public void ApplyEvent_returns_state_unchanged_for_unhandled_event_type()
    {
        var result = _projection.ApplyEvent(new Counter(42), new UnknownEvent());
        result.Should().Be(new Counter(42));
    }

    [Fact]
    public void ApplyEvent_does_not_invoke_base_fallback_for_handled_event_type()
    {
        var spy = new SpyProjection();
        spy.ApplyEvent(spy.InitialState(), new Incremented());
        spy.FallbackCalled.Should().BeFalse();
    }
}
