using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session;

const string ConnectionString = "your-connection-string-here";

var services = new ServiceCollection();

services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddWincheEvents(opts =>
{
    opts.ConnectionString = ConnectionString;
    opts.AddEvent<OrderPlaced>();
    opts.AddEvent<OrderShipped>();
    opts.AddEvent<OrderCancelled>();
    opts.AddProjection<OrderProjection>(ProjectionMode.Live);
    opts.AddNotifier<ConsoleNotifier>();
});

services.AddWincheEventsCommands(commands =>
{
    commands.AddHandler<PlaceOrderHandler>();
    commands.AddHandler<ShipOrderHandler>();
});

await using var provider = services.BuildServiceProvider();
var eventStore = provider.GetRequiredService<IEventStore>();
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
var logger = provider.GetRequiredService<ILogger<Program>>();

var orderId = $"orders/{Guid.NewGuid():N}";

logger.LogInformation("=== Place order via command dispatcher ===");
var order = await dispatcher.DispatchAsync<Order>(
    orderId, new PlaceOrderCommand(orderId, 99.99m));
logger.LogInformation("After place: status={Status} total={Total}", order?.Status, order?.Total);

logger.LogInformation("=== Ship order via command dispatcher ===");
order = await dispatcher.DispatchAsync<Order>(
    orderId, new ShipOrderCommand(orderId));
logger.LogInformation("After ship: status={Status}", order?.Status);

logger.LogInformation("=== Cancel directly via IEventSession ===");
await using (var session = await eventStore.OpenSessionAsync())
{
    await session.AppendAsync(orderId, [new OrderCancelled(orderId)]);
    await session.SaveChangesAsync();
}

logger.LogInformation("=== Load final state via IEventSession ===");
await using (var session = await eventStore.OpenSessionAsync())
{
    var finalState = await session.LoadAsync<Order>(orderId);
    logger.LogInformation("Final state: status={Status}", finalState?.Status);
}

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
    public override Order Create(string id) => Order.Empty with { Id = id };

    public Order Apply(Order state, OrderPlaced e) => state with { Status = "placed", Total = e.Total };
    public Order Apply(Order state, OrderShipped e) => state with { Status = "shipped" };
    public Order Apply(Order state, OrderCancelled e) => state with { Status = "cancelled" };
}

// ── Commands ──────────────────────────────────────────────────────────────────

record PlaceOrderCommand(string OrderId, decimal Total);
record ShipOrderCommand(string OrderId);

class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Order>
{
    public Task<IEnumerable<IEvent>> HandleAsync(PlaceOrderCommand cmd, Order? state, CancellationToken ct)
    {
        if (state is { Status: not "none" })
            throw new InvalidOperationException($"Order {cmd.OrderId} already exists.");

        return Task.FromResult<IEnumerable<IEvent>>([new OrderPlaced(cmd.OrderId, cmd.Total)]);
    }
}

class ShipOrderHandler : ICommandHandler<ShipOrderCommand, Order>
{
    public Task<IEnumerable<IEvent>> HandleAsync(ShipOrderCommand cmd, Order? state, CancellationToken ct)
    {
        if (state is null or { Status: "none" })
            throw new InvalidOperationException($"Order {cmd.OrderId} does not exist.");

        return Task.FromResult<IEnumerable<IEvent>>([new OrderShipped(cmd.OrderId)]);
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
