using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.DependencyInjection;
using Winche.Events.Projection;
using Xunit;
using WincheSession = Winche.Events.Session;

namespace Winche.Events.Tests.DependencyInjection;

record AliasedEvent : Event;
record AliasedAggregate(string Status) : Aggregate
{
    public static AliasedAggregate Empty => new("none");
}

class AliasedProjection : Projection<AliasedAggregate>
{
    public AliasedProjection()
    {
        On<AliasedEvent>((s, e) => s with { Status = "done" });
    }
    public override AliasedAggregate Create(string id) => AliasedAggregate.Empty with { Id = id };
}

public class WincheEventsRegistrationTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Database=winche_events_test;Username=postgres;Password=Ehsan1371";

    public async Task InitializeAsync()
    {
        var store = DocumentStore.For(opts =>
        {
            opts.Connection(ConnectionString);
            opts.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsString;
        });
        await store.Advanced.Clean.DeleteAllEventDataAsync();
        await store.DisposeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddEvent_with_alias_stores_events_under_that_type_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheEvents(opts =>
        {
            opts.ConnectionString = ConnectionString;
            opts.AddEvent<AliasedEvent>("aliased_event_v1");
            opts.AddProjection<AliasedProjection, AliasedAggregate>(ProjectionMode.Async);
        });
        var provider = services.BuildServiceProvider();
        var martenStore = provider.GetRequiredService<IDocumentStore>();
        var eventStore = provider.GetRequiredService<WincheSession.IEventStore>();
        await martenStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await using var session = await eventStore.OpenSessionAsync();
        await session.AppendAsync("alias-test/1", [new AliasedEvent()]);
        await session.SaveChangesAsync();

        using var q = martenStore.QuerySession();
        var events = await q.Events.FetchStreamAsync("alias-test/1");
        events.Should().HaveCount(1);
        events[0].EventTypeName.Should().Be("aliased_event_v1");
    }
}
