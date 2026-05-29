using FluentAssertions;
using Winche.Events.Abstractions;
using Xunit;

namespace Winche.Events.Commands.Tests;

record Thing(string Status) : Aggregate;
record CreateThing(string Id) : Command<Thing>;
record ActivateThing(string Id) : Command<Thing>;
record CancelThing(string Id) : Command<Thing>;
record ThingCreated(string Id) : Event;
record ThingActivated(string Id) : Event;

class ThingCommandHandler : CommandHandler<Thing>
{
    public ThingCommandHandler()
    {
        On<CreateThing>((state, cmd) =>
        {
            if (state is not null) throw new InvalidOperationException("Already exists.");
            return [new ThingCreated(cmd.Id)];
        });

        On<ActivateThing>(async (state, cmd) =>
        {
            await Task.Yield();
            return [new ThingActivated(cmd.Id)];
        });

        On<CancelThing>((state, cmd, ct) =>
            Task.FromResult<IEnumerable<IEvent>>([new ThingActivated(cmd.Id)]));
    }
}

class EmptyHandler : CommandHandler<Thing>
{
    // no On<> registrations
}

public class CommandHandlerTests
{
    private readonly ThingCommandHandler _handler = new();

    [Fact]
    public async Task Sync_handler_returns_events()
    {
        var events = await _handler.HandleAsync(null, new CreateThing("things/1"), default);
        events.Should().ContainSingle().Which.Should().BeOfType<ThingCreated>();
    }

    [Fact]
    public async Task Sync_handler_receives_current_state()
    {
        var state = new Thing("exists") { Id = "things/1" };
        var act = () => _handler.HandleAsync(state, new CreateThing("things/1"), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Already exists.");
    }

    [Fact]
    public async Task Async_handler_is_awaited()
    {
        var events = await _handler.HandleAsync(null, new ActivateThing("things/1"), default);
        events.Should().ContainSingle().Which.Should().BeOfType<ThingActivated>();
    }

    [Fact]
    public async Task Async_handler_with_cancellation_token_is_called()
    {
        using var cts = new CancellationTokenSource();
        var events = await _handler.HandleAsync(null, new CancelThing("things/1"), cts.Token);
        events.Should().ContainSingle().Which.Should().BeOfType<ThingActivated>();
    }

    [Fact]
    public async Task Unregistered_command_throws_InvalidOperationException()
    {
        var emptyHandler = new EmptyHandler();
        var act = () => emptyHandler.HandleAsync(null, new CreateThing("x"), default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CreateThing*");
    }
}
