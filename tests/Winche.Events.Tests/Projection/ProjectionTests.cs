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
        On<Incremented>(async (s,  e) =>
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
    public void ApplyEvent_uses_sync_handler_even_when_async_also_registered()
    {
        var p = new AsyncCounterProjection();
        var result = p.ApplyEvent(p.Create("test"), Envelope(new Incremented()));
        result.Value.Should().Be(1);
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
        var result = p.ApplyEvent(p.Create("counters/99"), new EventEnvelope<IEvent>("counters/99", new Incremented(), 7, ts));

        p.LastStreamId.Should().Be("counters/99");
        p.LastVersion.Should().Be(7);
        p.LastTimestamp.Should().Be(ts);
        result.Value.Should().Be(1);
    }

    [Fact]
    public void Method_group_is_accepted_as_handler()
    {
        var p = new MethodGroupProjection();
        var result = p.ApplyEvent(p.Create("test"), Envelope(new Incremented()));
        result.Value.Should().Be(5);
    }
}
