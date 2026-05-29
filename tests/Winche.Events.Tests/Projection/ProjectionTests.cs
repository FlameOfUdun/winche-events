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
        var envelope = new EventEnvelope<Incremented>(Guid.Empty.ToString(), "counters/1", data, 3, ts, 0, "", "");

        envelope.StreamId.Should().Be("counters/1");
        envelope.Data.Should().BeSameAs(data);
        envelope.Version.Should().Be(3);
        envelope.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void EventEnvelope_exposes_all_fields()
    {
        var id = Guid.NewGuid().ToString();
        var ts = new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);
        var data = new Incremented();
        var envelope = new EventEnvelope<Incremented>(id, "counters/5", data, 7, ts, 42, "incremented", "MyApp.Incremented, MyApp");

        envelope.Id.Should().Be(id);
        envelope.StreamId.Should().Be("counters/5");
        envelope.Data.Should().BeSameAs(data);
        envelope.Version.Should().Be(7);
        envelope.Timestamp.Should().Be(ts);
        envelope.Sequence.Should().Be(42);
        envelope.TypeAlias.Should().Be("incremented");
        envelope.DotNetType.Should().Be("MyApp.Incremented, MyApp");
    }

    [Fact]
    public void EventEnvelope_IEvent_holds_base_typed_data()
    {
        var data = new Incremented();
        var envelope = new EventEnvelope<IEvent>(Guid.Empty.ToString(), "counters/1", data, 1, DateTimeOffset.UtcNow, 0, "", "");

        envelope.Data.Should().BeSameAs(data);
        envelope.Data.Should().BeOfType<Incremented>();
    }

    [Fact]
    public void OfEventType_filters_to_matching_type()
    {
        var envelopes = new List<EventEnvelope<IEvent>>
        {
            new(Guid.Empty.ToString(), "s", new Incremented(), 1, DateTimeOffset.UtcNow, 1, "", ""),
            new(Guid.Empty.ToString(), "s", new Decremented(), 2, DateTimeOffset.UtcNow, 2, "", ""),
            new(Guid.Empty.ToString(), "s", new Incremented(), 3, DateTimeOffset.UtcNow, 3, "", ""),
        };

        var result = envelopes.OfEventType<Incremented>().ToList();

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(e => e.Data.Should().BeOfType<Incremented>());
    }

    [Fact]
    public void OfEventType_preserves_all_envelope_fields()
    {
        var id = Guid.NewGuid().ToString();
        var ts = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var data = new Incremented();
        var envelopes = new List<EventEnvelope<IEvent>>
        {
            new(id, "counters/9", data, 5, ts, 99, "incremented", "MyApp.Incremented"),
        };

        var typed = envelopes.OfEventType<Incremented>().Single();

        typed.Id.Should().Be(id);
        typed.StreamId.Should().Be("counters/9");
        typed.Data.Should().BeSameAs(data);
        typed.Version.Should().Be(5);
        typed.Timestamp.Should().Be(ts);
        typed.Sequence.Should().Be(99);
        typed.TypeAlias.Should().Be("incremented");
        typed.DotNetType.Should().Be("MyApp.Incremented");
    }

    [Fact]
    public void OfEventType_returns_empty_when_no_match()
    {
        var envelopes = new List<EventEnvelope<IEvent>>
        {
            new(Guid.Empty.ToString(), "s", new Decremented(), 1, DateTimeOffset.UtcNow, 1, "", ""),
        };

        envelopes.OfEventType<Incremented>().Should().BeEmpty();
    }
}

public class ProjectionTests
{
    private static EventEnvelope<IEvent> Envelope(IEvent data, string streamId = "test", long version = 1)
        => new(Guid.Empty.ToString(), streamId, data, version, DateTimeOffset.UtcNow, 0, "", "");

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
        var result = p.ApplyEvent(p.Create("counters/99"), new EventEnvelope<IEvent>(Guid.Empty.ToString(), "counters/99", new Incremented(), 7, ts, 0, "", ""));

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
