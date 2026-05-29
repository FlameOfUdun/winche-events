using FluentAssertions;
using NSubstitute;
using Winche.Events.Abstractions;
using Winche.Events.Projection.Internal;
using Xunit;
using JasperFxEvent = JasperFx.Events.IEvent;

namespace Winche.Events.Tests.Projection;

public class ProjectionBridgeTests
{
    private static JasperFxEvent MockEvent(IEvent data, long version = 1)
    {
        var e = Substitute.For<JasperFxEvent>();
        e.Data.Returns(data);
        e.Version.Returns(version);
        e.Timestamp.Returns(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return e;
    }

    [Fact]
    public void Evolve_creates_state_and_applies_sync_handler()
    {
        var bridge = new InlineProjectionBridge<Counter>(new CounterProjection());
        var result = bridge.Evolve(null, "test", MockEvent(new Incremented()));
        result.Should().Be(new Counter(1) { Id = "test" });
    }

    [Fact]
    public void Evolve_applies_to_existing_snapshot()
    {
        var bridge = new InlineProjectionBridge<Counter>(new CounterProjection());
        var snapshot = new Counter(5) { Id = "test" };
        var result = bridge.Evolve(snapshot, "test", MockEvent(new Incremented()));
        result.Should().Be(new Counter(6) { Id = "test" });
    }

    [Fact]
    public void Evolve_returns_unchanged_state_for_unregistered_event()
    {
        var bridge = new InlineProjectionBridge<Counter>(new CounterProjection());
        var snapshot = new Counter(42) { Id = "test" };
        var result = bridge.Evolve(snapshot, "test", MockEvent(new UnknownEvent()));
        result.Should().Be(new Counter(42) { Id = "test" });
    }

    [Fact]
    public async Task EvolveAsync_passes_envelope_with_correct_metadata()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var meta = new MetadataProjection();
        var bridge = new AsyncProjectionBridge<Counter>(meta);
        var jasperEvent = Substitute.For<JasperFxEvent>();
        jasperEvent.Data.Returns(new Incremented());
        jasperEvent.Version.Returns(7L);
        jasperEvent.Timestamp.Returns(ts);

        await bridge.EvolveAsync(null, "counters/99", Substitute.For<Marten.IQuerySession>(), jasperEvent, default);

        meta.LastVersion.Should().Be(7);
        meta.LastTimestamp.Should().Be(ts);
        meta.LastStreamId.Should().Be("counters/99");
    }
}
