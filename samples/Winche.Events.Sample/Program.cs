using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Models;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session;

const string ConnectionString = "your-connection-string-here";

var services = new ServiceCollection();

services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddWincheEvents(opts =>
{
    opts.ConnectionString = ConnectionString;
    opts.AddEventType<OrderPlaced>();
    opts.AddEventType<OrderShipped>();
    opts.AddEventType<OrderCancelled>();
    opts.AddProjection<OrderProjection, Order>(ProjectionMode.Live);
    opts.AddNotifier<ConsoleNotifier>();
});

services.AddWincheEventsCommands(commands =>
{
    commands.AddHandler<PlaceOrderCommand, Order, PlaceOrderHandler>();
    commands.AddHandler<ShipOrderCommand, Order, ShipOrderHandler>();
});

await using var provider = services.BuildServiceProvider();
var eventStore = provider.GetRequiredService<IEventStore>();
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
var logger = provider.GetRequiredService<ILogger<Program>>();

var orderId = $"orders/{Guid.NewGuid():N}";

logger.LogInformation("=== Place order via command dispatcher ===");
var order = await dispatcher.DispatchAsync<PlaceOrderCommand, Order>(
    orderId, new PlaceOrderCommand(orderId, 99.99m));
logger.LogInformation("After place: status={Status} total={Total}", order?.Status, order?.Total);

logger.LogInformation("=== Ship order via command dispatcher ===");
order = await dispatcher.DispatchAsync<ShipOrderCommand, Order>(
    orderId, new ShipOrderCommand(orderId));
logger.LogInformation("After ship: status={Status}", order?.Status);

logger.LogInformation("=== Cancel directly via IEventSession ===");
await using (var session = await eventStore.OpenSessionAsync())
{
    await session.AppendAsync<Order>(orderId, [new OrderCancelled(orderId)]);
    await session.SaveChangesAsync();
}

logger.LogInformation("=== Load final state via IEventSession ===");
await using (var session = await eventStore.OpenSessionAsync())
{
    var finalState = await session.LoadAsync<Order>(orderId);
    logger.LogInformation("Final state: status={Status}", finalState?.Status);
}

// ── Domain ────────────────────────────────────────────────────────────────────

record Order(string Id, string Status, decimal Total) : DomainEvent
{
    public static Order Empty => new(string.Empty, "none", 0);
}

record OrderPlaced(string OrderId, decimal Total) : DomainEvent;
record OrderShipped(string OrderId) : DomainEvent;
record OrderCancelled(string OrderId) : DomainEvent;

class OrderProjection : Projection<Order>
{
    public override Order InitialState() => Order.Empty;

    public Order Apply(Order state, OrderPlaced e) => state with { Id = e.OrderId, Status = "placed", Total = e.Total };
    public Order Apply(Order state, OrderShipped e) => state with { Status = "shipped" };
    public Order Apply(Order state, OrderCancelled e) => state with { Status = "cancelled" };
}

// ── Commands ──────────────────────────────────────────────────────────────────

record PlaceOrderCommand(string OrderId, decimal Total);
record ShipOrderCommand(string OrderId);

class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Order>
{
    public Task<IEnumerable<DomainEvent>> HandleAsync(PlaceOrderCommand cmd, Order? state, CancellationToken ct)
    {
        if (state is { Status: not "none" })
            throw new InvalidOperationException($"Order {cmd.OrderId} already exists.");

        return Task.FromResult<IEnumerable<DomainEvent>>([new OrderPlaced(cmd.OrderId, cmd.Total)]);
    }
}

class ShipOrderHandler : ICommandHandler<ShipOrderCommand, Order>
{
    public Task<IEnumerable<DomainEvent>> HandleAsync(ShipOrderCommand cmd, Order? state, CancellationToken ct)
    {
        if (state is null or { Status: "none" })
            throw new InvalidOperationException($"Order {cmd.OrderId} does not exist.");

        return Task.FromResult<IEnumerable<DomainEvent>>([new OrderShipped(cmd.OrderId)]);
    }
}

// ── Notifier ──────────────────────────────────────────────────────────────────

class ConsoleNotifier(ILogger<ConsoleNotifier> logger) : IAppendNotifier
{
    public Task NotifyAsync(string streamId, string streamType, IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
            logger.LogInformation("[notify] {Stream} → {Event}", streamId, e.GetType().Name);
        return Task.CompletedTask;
    }
}
