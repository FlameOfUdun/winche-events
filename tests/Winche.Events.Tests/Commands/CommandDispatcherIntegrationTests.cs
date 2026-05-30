using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Commands;
using Winche.Events.Commands.DependencyInjection;
using Winche.Events.DependencyInjection;
using Winche.Events.Projection;
using Xunit;

namespace Winche.Events.Tests.Commands;

record Item(string Status) : Aggregate;
record CreateItem(string ItemId) : Command<Item>;
record ActivateItem(string ItemId) : Command<Item>;
record ItemCreated(string ItemId) : Event;
record ItemActivated(string ItemId) : Event;

class ItemProjection : Projection<Item>
{
    public ItemProjection()
    {
        On<ItemCreated>((s, e) => s with { Status = "created" });
        On<ItemActivated>((s, e) => s with { Status = "activated" });
    }
    public override Item Create(string id) => new Item("none") { Id = id };
}

class ItemCommandHandler : CommandHandler<Item>
{
    public ItemCommandHandler()
    {
        On<CreateItem>((state, cmd) =>
        {
            if (state is not null) throw new InvalidOperationException("Already exists.");
            return [new ItemCreated(cmd.ItemId)];
        });
        On<ActivateItem>((state, cmd) => [new ItemActivated(cmd.ItemId)]);
    }
}

public class CommandDispatcherIntegrationTests : IAsyncLifetime
{
    private IDocumentStore _martenStore = null!;
    private ICommandDispatcher _dispatcher = null!;

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("WINCHE_TEST_CONN") ?? "your-connection-string-here";

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheEvents(opts =>
        {
            opts.ConnectionString = ConnectionString;
            opts.AddEvent<ItemCreated>();
            opts.AddEvent<ItemActivated>();
            opts.AddProjection<ItemProjection, Item>(ProjectionMode.Inline);
        });
        services.AddWincheEventsCommands(cmds =>
        {
            cmds.AddCommandHandler<ItemCommandHandler, Item>();
        });

        var provider = services.BuildServiceProvider();
        _martenStore = provider.GetRequiredService<IDocumentStore>();
        _dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await _martenStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await _martenStore.Advanced.Clean.DeleteAllEventDataAsync();
        await _martenStore.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Item));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DispatchAsync_returns_server_events_with_full_metadata()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await _dispatcher.DispatchAsync("items/1", new CreateItem("items/1"));

        result.Events.Should().ContainSingle();
        result.Version.Should().Be(1);

        var e = result.Events[0];
        e.Id.Should().NotBeNullOrEmpty();
        e.StreamId.Should().Be("items/1");
        e.Data.Should().BeOfType<ItemCreated>();
        e.Version.Should().Be(1);
        e.Timestamp.Should().BeAfter(before);
        e.Sequence.Should().BeGreaterThan(0);
        e.TypeAlias.Should().NotBeNullOrEmpty();
        e.DotNetType.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DispatchAsync_returns_correct_version_after_multiple_commands()
    {
        await _dispatcher.DispatchAsync("items/2", new CreateItem("items/2"));
        var result = await _dispatcher.DispatchAsync("items/2", new ActivateItem("items/2"));

        result.Version.Should().Be(2);
        result.Events.Should().ContainSingle().Which.Data.Should().BeOfType<ItemActivated>();
    }

    [Fact]
    public async Task DispatchAsync_throws_on_wrong_expectedVersion()
    {
        await _dispatcher.DispatchAsync("items/3", new CreateItem("items/3")); // version now 1

        var act = () => _dispatcher.DispatchAsync(
            "items/3",
            new ActivateItem("items/3") { ExpectedVersion = 0 }); // wrong — stream is at 1

        await act.Should().ThrowAsync<Exception>();
    }
}
