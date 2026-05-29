using FluentAssertions;
using JasperFx.Events;
using NSubstitute;
using Winche.Events.Projection.Internal;
using Xunit;

namespace Winche.Events.Tests.Projection;

public class ProjectionBridgeTests
{
    private static IEvent EventWith(object data)
    {
        var e = Substitute.For<IEvent>();
        e.Data.Returns(data);
        return e;
    }

    [Fact]
    public void Evolve_calls_Create_when_snapshot_is_null()
    {
        var bridge = new ProjectionBridge<Counter>(new CounterProjection());

        var result = bridge.Evolve(null, "test", EventWith(new Incremented()));

        result.Should().Be(new Counter(1) { Id = "test" }); // Create gives (0,id), then +1
    }

    [Fact]
    public void Evolve_applies_event_to_existing_snapshot()
    {
        var bridge = new ProjectionBridge<Counter>(new CounterProjection());
        var snapshot = new Counter(5) { Id = "test" };

        var result = bridge.Evolve(snapshot, "test", EventWith(new Incremented()));

        result.Should().Be(new Counter(6) { Id = "test" });
    }

    [Fact]
    public void Evolve_returns_state_unchanged_for_unhandled_event_type()
    {
        var bridge = new ProjectionBridge<Counter>(new CounterProjection());
        var snapshot = new Counter(42) { Id = "test" };

        var result = bridge.Evolve(snapshot, "test", EventWith(new UnknownEvent()));

        result.Should().Be(new Counter(42) { Id = "test" });
    }
}
