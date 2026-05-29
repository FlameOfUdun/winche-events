using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.DependencyInjection;
using Xunit;

namespace Winche.Events.Tests.DependencyInjection;

record SomeEvent : Event;
record OtherEvent : Event;

public class WincheEventsOptionsTests
{
    // ── AddEvent ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddEvent_without_alias_stores_null_alias()
    {
        var opts = new WincheEventsOptions();

        opts.AddEvent<SomeEvent>();

        opts.EventTypes.Should().ContainSingle()
            .Which.Should().Be((typeof(SomeEvent), (string?)null));
    }

    [Fact]
    public void AddEvent_with_alias_stores_that_alias()
    {
        var opts = new WincheEventsOptions();

        opts.AddEvent<SomeEvent>("some_event");

        opts.EventTypes.Should().ContainSingle()
            .Which.Should().Be((typeof(SomeEvent), "some_event"));
    }

    [Fact]
    public void AddEvent_multiple_registrations_are_all_preserved()
    {
        var opts = new WincheEventsOptions();

        opts.AddEvent<SomeEvent>("some_event");
        opts.AddEvent<OtherEvent>();

        opts.EventTypes.Should().HaveCount(2);
        opts.EventTypes.Should().Contain((typeof(SomeEvent), "some_event"));
        opts.EventTypes.Should().Contain((typeof(OtherEvent), (string?)null));
    }

    // ── ConfigureJsonSerializer ───────────────────────────────────────────────

    [Fact]
    public void ConfigureJsonSerializer_is_null_by_default()
    {
        var opts = new WincheEventsOptions();

        opts.ConfigureJsonSerializer.Should().BeNull();
    }

    [Fact]
    public void ConfigureJsonSerializer_stores_the_provided_action()
    {
        var opts = new WincheEventsOptions();
        Action<JsonSerializerOptions> action = _ => { };

        opts.ConfigureJsonSerializer = action;

        opts.ConfigureJsonSerializer.Should().BeSameAs(action);
    }

    // ── DI smoke test (no database required) ─────────────────────────────────

    [Fact]
    public void ConfigureJsonSerializer_action_is_invoked_during_store_setup()
    {
        // UseSystemTextJsonForSerialization calls the configure action immediately,
        // so the spy fires when the IDocumentStore singleton is first resolved —
        // no database connection is opened at that point.
        var wasCalled = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheEvents(opts =>
        {
            opts.ConnectionString = "Host=localhost;Database=test;Username=x;Password=y";
            opts.AddEvent<SomeEvent>("some_event");
            opts.ConfigureJsonSerializer = _ => { wasCalled = true; };
        });

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDocumentStore>();

        wasCalled.Should().BeTrue();
    }
}
