using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Notification;
using Winche.Events.Projection;
using Winche.Events.Session;
using System.Text.Json;
using System.Text.Json.Serialization;

const string ConnectionString = "Host=localhost;Port=5432;Username=postgres;Password=Ehsan1371;Database=winche_events_test";

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddWincheEvents(opts =>
{
    opts.ConnectionString = ConnectionString;
    opts.AddEvent<OrderPlaced>();
    opts.AddEvent<OrderShipped>();
    opts.AddEvent<OrderCancelled>();
    opts.AddProjection<OrderProjection, Order>(ProjectionMode.Inline);
    opts.AddNotifier<ConsoleNotifier>();
    opts.ConfigureJsonSerializer = jsonOpts =>
    {
        jsonOpts.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        jsonOpts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    };
});

services.AddWincheEventsCommands(cmds =>
{
    cmds.AddCommandHandler<OrderCommandHandler, Order>();
});

await using var provider = services.BuildServiceProvider();
var eventStore = provider.GetRequiredService<IEventStore>();
var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
var logger = provider.GetRequiredService<ILogger<Program>>();

var orderId = $"orders/{Guid.NewGuid():N}";

logger.LogInformation("=== Place order ===");
await dispatcher.DispatchAsync(orderId, new PlaceOrderCommand(orderId, 99.99m));
await using var s1 = await eventStore.OpenSessionAsync();
var order = await s1.LoadAsync<Order>(orderId);
logger.LogInformation("After place: status={Status} total={Total}", order?.Status, order?.Total);

logger.LogInformation("=== Ship order ===");
await dispatcher.DispatchAsync(orderId, new ShipOrderCommand(orderId));
await using var s2 = await eventStore.OpenSessionAsync();
order = await s2.LoadAsync<Order>(orderId);
logger.LogInformation("After ship: status={Status}", order?.Status);

logger.LogInformation("=== Cancel directly ===");
await using (var session = await eventStore.OpenSessionAsync())
{
    await session.AppendAsync(orderId, [new OrderCancelled(orderId)]);
    await session.SaveChangesAsync();
}

logger.LogInformation("=== Read all events ===");
await using var evtSession = await eventStore.OpenSessionAsync();
var events = await evtSession.GetEventsAsync(orderId);
foreach (var e in events)
    logger.LogInformation("v{Version} [{Timestamp}] {Type}", e.Version, e.Timestamp, e.Data.GetType().Name);

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
    public OrderProjection()
    {
        On<OrderPlaced>((s, e) => s with { Status = "placed", Total = e.Data.Total });
        On<OrderShipped>((s, e) => s with { Status = "shipped" });
        On<OrderCancelled>((s, e) => s with { Status = "cancelled" });
    }
    public override Order Create(string id) => Order.Empty with { Id = id };
}

// ── Commands ──────────────────────────────────────────────────────────────────

record PlaceOrderCommand(string OrderId, decimal Total) : Command<Order>;
record ShipOrderCommand(string OrderId) : Command<Order>;

class OrderCommandHandler : CommandHandler<Order>
{
    public OrderCommandHandler()
    {
        On<PlaceOrderCommand>((state, cmd) =>
        {
            if (state is { Status: not "none" })
                throw new InvalidOperationException($"Order {cmd.OrderId} already exists.");
            return [new OrderPlaced(cmd.OrderId, cmd.Total)];
        });

        On<ShipOrderCommand>((state, cmd) =>
        {
            if (state is null or { Status: "none" })
                throw new InvalidOperationException($"Order {cmd.OrderId} does not exist.");
            return [new OrderShipped(cmd.OrderId)];
        });
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
