using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Models;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session;
using Xunit;
using WincheSession = Winche.Events.Session;

namespace Winche.Events.Tests.Session;

public record OrderState(string Id, string Status) : DomainEvent;
public record OrderPlaced(string OrderId) : DomainEvent;
public record OrderShipped(string OrderId) : DomainEvent;

public class OrderProjection : Projection<OrderState>
{
    public override OrderState InitialState() => new OrderState(string.Empty, "none");
    public OrderState Apply(OrderState s, OrderPlaced e) => s with { Id = e.OrderId, Status = "placed" };
    public OrderState Apply(OrderState s, OrderShipped e) => s with { Status = "shipped" };
}

class CapturingNotifier : IAppendNotifier
{
    public List<(string StreamId, string StreamType, IReadOnlyList<DomainEvent> Events)> Calls { get; } = [];

    public Task NotifyAsync(string streamId, string streamType, IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        Calls.Add((streamId, streamType, events));
        return Task.CompletedTask;
    }
}

public class EventSessionTests : IAsyncLifetime
{
    private IDocumentStore _martenStore = null!;
    private IEventStore _eventStore = null!;
    private CapturingNotifier _notifier = null!;

    private const string ConnectionString =
        "Host=localhost;Database=winche_events_test;Username=postgres;Password=Ehsan1371";

    public async Task InitializeAsync()
    {
        _notifier = new CapturingNotifier();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppendNotifier>(_notifier);
        services.AddWincheEvents(opts =>
        {
            opts.ConnectionString = ConnectionString;
            opts.AddEventType<OrderPlaced>();
            opts.AddEventType<OrderShipped>();
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
        await session.AppendAsync<OrderState>("orders/1", [new OrderPlaced("orders/1")]);
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
        await session.AppendAsync<OrderState>("orders/2", [placed]);
        await session.SaveChangesAsync();

        _notifier.Calls.Should().HaveCount(1);
        _notifier.Calls[0].StreamId.Should().Be("orders/2");
        _notifier.Calls[0].StreamType.Should().Be(nameof(OrderState));
        _notifier.Calls[0].Events.Should().ContainSingle().Which.Should().Be(placed);
    }

    [Fact]
    public async Task LoadAsync_live_aggregation_folds_events_correctly()
    {
        await using var session = await _eventStore.OpenSessionAsync();
        await session.AppendAsync<OrderState>("orders/3", [new OrderPlaced("orders/3"), new OrderShipped("orders/3")]);
        await session.SaveChangesAsync();

        await using var readSession = await _eventStore.OpenSessionAsync();
        var state = await readSession.LoadAsync<OrderState>("orders/3");

        state.Should().NotBeNull();
        state!.Status.Should().Be("shipped");
    }

    [Fact]
    public async Task Disposing_without_SaveChangesAsync_rolls_back()
    {
        await using (var session = await _eventStore.OpenSessionAsync())
        {
            await session.AppendAsync<OrderState>("orders/4", [new OrderPlaced("orders/4")]);
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
        await setup.AppendAsync<OrderState>("orders/5", [new OrderPlaced("orders/5")]);
        await setup.SaveChangesAsync(); // stream now at version 1

        await using var conflictSession = await _eventStore.OpenSessionAsync();
        await conflictSession.AppendAsync<OrderState>("orders/5", [new OrderShipped("orders/5")], expectedVersion: 0);

        Func<Task> act = () => conflictSession.SaveChangesAsync();
        await act.Should().ThrowAsync<Exception>();
    }
}
