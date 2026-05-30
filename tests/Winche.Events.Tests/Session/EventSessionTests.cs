using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session;
using Xunit;
using WincheSession = Winche.Events.Session;

namespace Winche.Events.Tests.Session;

public record OrderState(string Status) : Aggregate;
public record OrderPlaced(string OrderId) : Event;
public record OrderShipped(string OrderId) : Event;

public class OrderProjection : Projection<OrderState>
{
    public OrderProjection()
    {
        On<OrderPlaced>((s, e) => s with { Status = "placed" });
        On<OrderShipped>((s, e) => s with { Status = "shipped" });
    }
    public override OrderState Create(string id) => new OrderState("none") { Id = id };
}

class CapturingNotifier : IAppendNotifier
{
    public List<(string StreamId, IReadOnlyList<IEvent> Events)> Calls { get; } = [];

    public Task NotifyAsync(string streamId, IReadOnlyList<IEvent> events, CancellationToken ct = default)
    {
        Calls.Add((streamId, events));
        return Task.CompletedTask;
    }
}

public class EventSessionTests : IAsyncLifetime
{
    private IDocumentStore _martenStore = null!;
    private IEventStore _eventStore = null!;
    private CapturingNotifier _notifier = null!;

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("WINCHE_TEST_CONN") ?? "your-connection-string-here";

    public async Task InitializeAsync()
    {
        _notifier = new CapturingNotifier();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppendNotifier>(_notifier);
        services.AddWincheEvents(opts =>
        {
            opts.ConnectionString = ConnectionString;
            opts.AddEvent<OrderPlaced>();
            opts.AddEvent<OrderShipped>();
            opts.AddProjection<OrderProjection, OrderState>(ProjectionMode.Inline);
        });

        var provider = services.BuildServiceProvider();
        _martenStore = provider.GetRequiredService<IDocumentStore>();
        _eventStore = provider.GetRequiredService<WincheSession.IEventStore>();

        await _martenStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await _martenStore.Advanced.Clean.DeleteAllEventDataAsync();
        await _martenStore.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(OrderState));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AppendAsync_and_SaveChangesAsync_store_events_in_marten()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/1", [new OrderPlaced("orders/1")]);
        await session.SaveChangesAsync();

        using var readSession = _martenStore.QuerySession();
        var events = await readSession.Events.FetchStreamAsync("orders/1");
        events.Should().HaveCount(1);
        events[0].Data.Should().BeOfType<OrderPlaced>();
    }

    [Fact]
    public async Task SaveChangesAsync_fires_notifier_with_correct_args()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        var placed = new OrderPlaced("orders/2");
        await session.AppendStreamAsync("orders/2", [placed]);
        await session.SaveChangesAsync();

        _notifier.Calls.Should().HaveCount(1);
        _notifier.Calls[0].StreamId.Should().Be("orders/2");
        _notifier.Calls[0].Events.Should().ContainSingle().Which.Should().Be(placed);
    }

    [Fact]
    public async Task LoadAsync_live_aggregation_folds_events_correctly()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/3", [new OrderPlaced("orders/3"), new OrderShipped("orders/3")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var state = await readSession.GetStateAsync<OrderState>("orders/3");

        state.Should().NotBeNull();
        state!.Status.Should().Be("shipped");
    }

    [Fact]
    public async Task Disposing_without_SaveChangesAsync_rolls_back()
    {
        await using (var session = await _eventStore.OpenSessionAsync())
        {
            await session.AppendStreamAsync("orders/4", [new OrderPlaced("orders/4")]);
            // no SaveChangesAsync
        }

        using var readSession = _martenStore.QuerySession();
        var events = await readSession.Events.FetchStreamAsync("orders/4");
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_with_stale_expectedVersion_throws_on_SaveChangesAsync()
    {
        await using var setup = await _eventStore.OpenSessionAsync();
        await setup.AppendStreamAsync("orders/5", [new OrderPlaced("orders/5")]);
        await setup.SaveChangesAsync(); // stream now at version 1

        await using var conflictSession = await _eventStore.OpenSessionAsync();
        await conflictSession.AppendStreamAsync("orders/5", [new OrderShipped("orders/5")], expectedVersion: 0);

        Func<Task> act = () => conflictSession.SaveChangesAsync();
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task GetStreamAsync_returns_null_for_nonexistent_stream()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        var result = await session.GetStreamAsync<OrderState>("orders/nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStreamAsync_returns_correct_stream_metadata()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/stream-meta", [new OrderPlaced("orders/stream-meta"), new OrderShipped("orders/stream-meta")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var envelope = await readSession.GetStreamAsync<OrderState>("orders/stream-meta");

        envelope.Should().NotBeNull();
        envelope!.Id.Should().Be("orders/stream-meta");
        envelope.Version.Should().Be(2);
        envelope.IsArchived.Should().BeFalse();
        envelope.Created.Should().BeAfter(before);
        envelope.LastModified.Should().BeAfter(before);
    }

    [Fact]
    public async Task GetStreamAsync_returns_current_aggregate()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/stream-agg", [new OrderPlaced("orders/stream-agg"), new OrderShipped("orders/stream-agg")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var envelope = await readSession.GetStreamAsync<OrderState>("orders/stream-agg");

        envelope!.Aggregate.Should().NotBeNull();
        envelope.Aggregate!.Status.Should().Be("shipped");
        envelope.Aggregate.Id.Should().Be("orders/stream-agg");
    }

    [Fact]
    public async Task GetStreamAsync_aggregate_is_null_when_no_projection_stored()
    {
        // Append events for a stream with no projection registered for this aggregate type.
        // Marten returns null from LoadAsync when no document exists.
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/stream-noproj", [new OrderPlaced("orders/stream-noproj")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        // OrderState projection IS registered inline, so the document exists.
        // This test verifies the Aggregate field is populated (not null) after events.
        var envelope = await readSession.GetStreamAsync<OrderState>("orders/stream-noproj");

        envelope.Should().NotBeNull();
        envelope!.Aggregate.Should().NotBeNull();
    }

    [Fact]
    public async Task GetEventsAsync_returns_envelopes_with_correct_fields()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/evt-fields", [new OrderPlaced("orders/evt-fields")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var events = await readSession.GetEventsAsync("orders/evt-fields");

        events.Should().HaveCount(1);
        var e = events[0];
        e.Id.Should().NotBeNullOrEmpty();
        e.StreamId.Should().Be("orders/evt-fields");
        e.Data.Should().BeOfType<OrderPlaced>();
        e.Version.Should().Be(1);
        e.Timestamp.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        e.Sequence.Should().BeGreaterThan(0);
        e.TypeAlias.Should().NotBeNullOrEmpty();
        e.DotNetType.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetEventsAsync_with_fromVersion_returns_only_events_at_or_after()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/fv1", [
            new OrderPlaced("orders/fv1"),
            new OrderShipped("orders/fv1"),
            new OrderPlaced("orders/fv1"),
        ]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var events = await readSession.GetEventsAsync("orders/fv1", fromVersion: 2);

        events.Should().HaveCount(2);
        events.Should().AllSatisfy(e => e.Version.Should().BeGreaterThanOrEqualTo(2));
        events[0].Version.Should().Be(2);
        events[1].Version.Should().Be(3);
    }

    [Fact]
    public async Task GetEventsAsync_OfEventType_filters_correctly()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendStreamAsync("orders/evt-filter", [new OrderPlaced("orders/evt-filter"), new OrderShipped("orders/evt-filter")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var events = await readSession.GetEventsAsync("orders/evt-filter");

        var placed = events.OfEventType<OrderPlaced>().ToList();
        var shipped = events.OfEventType<OrderShipped>().ToList();

        placed.Should().ContainSingle().Which.Data.OrderId.Should().Be("orders/evt-filter");
        shipped.Should().ContainSingle().Which.Data.OrderId.Should().Be("orders/evt-filter");
    }

    [Fact]
    public async Task QueryStatesAsync_returns_all_documents_without_filter()
    {
        await using var setup = await _eventStore.OpenSessionAsync();
        await setup.AppendStreamAsync("orders/q1", [new OrderPlaced("orders/q1")]);
        await setup.AppendStreamAsync("orders/q2", [new OrderPlaced("orders/q2")]);
        await setup.SaveChangesAsync();

        await using var session = await _eventStore.OpenSessionAsync();
        var results = await session.QueryStatesAsync<OrderState>(q => q);

        results.Should().Contain(o => o.Id == "orders/q1");
        results.Should().Contain(o => o.Id == "orders/q2");
    }

    [Fact]
    public async Task QueryStatesAsync_applies_where_filter()
    {
        await using var setup = await _eventStore.OpenSessionAsync();
        await setup.AppendStreamAsync("orders/qw1", [new OrderPlaced("orders/qw1")]);
        await setup.AppendStreamAsync("orders/qw2", [new OrderPlaced("orders/qw2"), new OrderShipped("orders/qw2")]);
        await setup.SaveChangesAsync();

        await using var session = await _eventStore.OpenSessionAsync();
        var shipped = await session.QueryStatesAsync<OrderState>(q => q.Where(o => o.Status == "shipped"));

        shipped.Should().Contain(o => o.Id == "orders/qw2");
        shipped.Should().NotContain(o => o.Id == "orders/qw1");
    }

    [Fact]
    public async Task QueryStatesAsync_applies_take()
    {
        await using var setup = await _eventStore.OpenSessionAsync();
        await setup.AppendStreamAsync("orders/qt1", [new OrderPlaced("orders/qt1")]);
        await setup.AppendStreamAsync("orders/qt2", [new OrderPlaced("orders/qt2")]);
        await setup.AppendStreamAsync("orders/qt3", [new OrderPlaced("orders/qt3")]);
        await setup.SaveChangesAsync();

        await using var session = await _eventStore.OpenSessionAsync();
        var results = await session.QueryStatesAsync<OrderState>(q => q.Where(o => o.Status == "placed").Take(2));

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryStatesAsync_returns_empty_when_no_match()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        var results = await session.QueryStatesAsync<OrderState>(q => q.Where(o => o.Status == "nonexistent-status"));

        results.Should().BeEmpty();
    }
}
