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
    }

    private CommandDispatcher BuildDispatcher()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(CommandHandler<Thing>)).Returns(_handler);
        return new CommandDispatcher(_store, sp);
    }

    [Fact]
    public async Task DispatchAsync_passes_null_state_to_handler_for_new_stream()
    {
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

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
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        await BuildDispatcher().DispatchAsync("things/2", new CreateThing("things/2"));

        await _session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_passes_expectedVersion_to_AppendAsync()
    {
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(new Thing("exists") { Id = "things/3" }));

        await BuildDispatcher().DispatchAsync("things/3", new ActivateThing("things/3"), expectedVersion: 5);

        await _session.Received(1).AppendStreamAsync(
            "things/3",
            Arg.Any<IEnumerable<IEvent>>(),
            (long?)5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_does_not_return_state_and_LoadAsync_called_once()
    {
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        await BuildDispatcher().DispatchAsync("things/4", new CreateThing("things/4"));

        // LoadAsync called only once (pre-dispatch), not again after commit
        await _session.Received(1).GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_propagates_handler_exception_without_appending()
    {
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(new Thing("exists") { Id = "things/x" }));

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
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        var sp = Substitute.For<IServiceProvider>(); // returns null for all GetService
        var dispatcher = new CommandDispatcher(_store, sp);

        var act = () => dispatcher.DispatchAsync("things/x", new CreateThing("things/x"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DispatchAsync_disposes_session()
    {
        _session.GetStateAsync<Thing>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Thing?>(null));

        await BuildDispatcher().DispatchAsync("things/5", new CreateThing("things/5"));

        await _session.Received(1).DisposeAsync();
    }
}
