using FluentAssertions;
using NSubstitute;
using Winche.Events.Abstractions;
using Winche.Events.Commands.Internal;
using Winche.Events.Session;
using Xunit;

namespace Winche.Events.Commands.Tests;

public class CommandDispatcherTests
{
    private readonly IEventSession _session;
    private readonly IEventStore _store;
    private readonly ThingCommandHandler _handler = new();

    public CommandDispatcherTests()
    {
        _session = Substitute.For<IEventSession>();
        _store = Substitute.For<IEventStore>();
        _store.OpenSessionAsync(default, default).ReturnsForAnyArgs(Task.FromResult(_session));
        _session.GetEventsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EventEnvelope<IEvent>>>([]));
    }

    private CommandDispatcher BuildDispatcher()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(CommandHandler<Thing>)).Returns(_handler);
        return new CommandDispatcher(_store, sp);
    }

    private static StreamEnvelope<Thing>? NullEnvelope() => null;

    private static StreamEnvelope<Thing> ExistingEnvelope(string id, Thing aggregate, long version = 1)
        => new(id, aggregate, version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, "Thing");

    [Fact]
    public async Task DispatchAsync_passes_null_state_to_handler_for_new_stream()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NullEnvelope()));

        await BuildDispatcher().DispatchAsync("things/1", new CreateThing("things/1"));

        await _session.Received(1).AppendStreamAsync(
            "things/1",
            Arg.Is<IEnumerable<IEvent>>(e => e.OfType<ThingCreated>().Any()),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_calls_SaveChangesAsync()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NullEnvelope()));

        await BuildDispatcher().DispatchAsync("things/2", new CreateThing("things/2"));

        await _session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_passes_expectedVersion_to_AppendAsync()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StreamEnvelope<Thing>?>(
                ExistingEnvelope("things/3", new Thing("exists") { Id = "things/3" }, version: 5)));

        await BuildDispatcher().DispatchAsync("things/3", new ActivateThing("things/3") { ExpectedVersion = 5 });

        await _session.Received(1).AppendStreamAsync(
            "things/3",
            Arg.Any<IEnumerable<IEvent>>(),
            (long?)5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_calls_GetStreamAsync_once()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NullEnvelope()));

        await BuildDispatcher().DispatchAsync("things/4", new CreateThing("things/4"));

        await _session.Received(1).GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_fetches_events_from_version_after_previous()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StreamEnvelope<Thing>?>(
                ExistingEnvelope("things/5", new Thing("exists") { Id = "things/5" }, version: 3)));

        await BuildDispatcher().DispatchAsync("things/5", new ActivateThing("things/5"));

        await _session.Received(1).GetEventsAsync("things/5", 4L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_returns_events_and_version_from_server()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NullEnvelope()));

        var serverEvent = new EventEnvelope<IEvent>(
            Guid.NewGuid().ToString(), "things/6", new ThingCreated("things/6"), 1,
            DateTimeOffset.UtcNow, 100, "thing_created", "ThingCreated");

        _session.GetEventsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EventEnvelope<IEvent>>>([serverEvent]));

        var result = await BuildDispatcher().DispatchAsync("things/6", new CreateThing("things/6"));

        result.Events.Should().ContainSingle().Which.Should().Be(serverEvent);
        result.Version.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_propagates_handler_exception_without_appending()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StreamEnvelope<Thing>?>(
                ExistingEnvelope("things/x", new Thing("exists") { Id = "things/x" })));

        // CreateThing handler throws when state is not null
        var act = () => BuildDispatcher().DispatchAsync("things/x", new CreateThing("things/x"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Already exists.");

        await _session.DidNotReceive().AppendStreamAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<IEvent>>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_propagates_exception_when_no_handler_registered()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NullEnvelope()));

        var sp = Substitute.For<IServiceProvider>(); // returns null for all GetService
        var dispatcher = new CommandDispatcher(_store, sp);

        var act = () => dispatcher.DispatchAsync("things/x", new CreateThing("things/x"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DispatchAsync_disposes_session()
    {
        _session.GetStreamAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NullEnvelope()));

        await BuildDispatcher().DispatchAsync("things/5", new CreateThing("things/5"));

        await _session.Received(1).DisposeAsync();
    }
}
