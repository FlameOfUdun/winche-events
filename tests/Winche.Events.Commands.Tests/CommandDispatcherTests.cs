using FluentAssertions;
using NSubstitute;
using Winche.Events.Commands;
using Winche.Events.Commands.Internal;
using Winche.Events.Abstractions;
using Winche.Events.Session;
using Xunit;

namespace Winche.Events.Commands.Tests;

record Widget(int Count) : Aggregate;
record WidgetCreated(string Id) : Event;
record WidgetIncremented : Event;

class CreateWidgetCommand(string Id) { public string Id { get; } = Id; }
class IncrementWidgetCommand;

class CreateWidgetHandler : ICommandHandler<CreateWidgetCommand, Widget>
{
    public Task<IEnumerable<IEvent>> HandleAsync(CreateWidgetCommand cmd, Widget? state, CancellationToken ct)
    {
        if (state != null) throw new InvalidOperationException("Widget already exists.");
        return Task.FromResult<IEnumerable<IEvent>>([new WidgetCreated(cmd.Id)]);
    }
}

class ThrowingHandler : ICommandHandler<CreateWidgetCommand, Widget>
{
    public Task<IEnumerable<IEvent>> HandleAsync(CreateWidgetCommand cmd, Widget? state, CancellationToken ct)
        => throw new InvalidOperationException("rejected");
}

class IncrementWidgetHandler : ICommandHandler<IncrementWidgetCommand, Widget>
{
    public Task<IEnumerable<IEvent>> HandleAsync(IncrementWidgetCommand cmd, Widget? state, CancellationToken ct)
    {
        if (state == null) return Task.FromResult(Enumerable.Empty<IEvent>());
        return Task.FromResult<IEnumerable<IEvent>>([new WidgetIncremented()]);
    }
}

public class CommandDispatcherTests
{
    private readonly IEventSession _session;
    private readonly IEventStore _store;
    private readonly CreateWidgetHandler _createHandler = new();
    private readonly IncrementWidgetHandler _incrementHandler = new();

    public CommandDispatcherTests()
    {
        _session = Substitute.For<IEventSession>();
        _store = Substitute.For<IEventStore>();
        _store.OpenSessionAsync(default, default)
              .ReturnsForAnyArgs(Task.FromResult(_session));
    }

    private CommandDispatcher BuildDispatcher() =>
        new CommandDispatcher(_store, type =>
        {
            if (type == typeof(ICommandHandler<CreateWidgetCommand, Widget>)) return _createHandler;
            if (type == typeof(ICommandHandler<IncrementWidgetCommand, Widget>)) return _incrementHandler;
            throw new InvalidOperationException($"No handler for {type}");
        });

    [Fact]
    public async Task DispatchAsync_passes_null_state_to_handler_for_new_stream()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(new Widget(0) { Id = "widgets/1" }));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<Widget>("widgets/1", new CreateWidgetCommand("widgets/1"));

        await _session.Received(1).AppendAsync(
            "widgets/1",
            Arg.Is<IEnumerable<IEvent>>(e => e.OfType<WidgetCreated>().Any()),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_calls_SaveChangesAsync_after_append()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(new Widget(0) { Id = "widgets/2" }));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<Widget>("widgets/2", new CreateWidgetCommand("widgets/2"));

        await _session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_passes_expectedVersion_to_AppendAsync()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(new Widget(1) { Id = "widgets/3" }),
                Task.FromResult<Widget?>(new Widget(2) { Id = "widgets/3" }));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<Widget>("widgets/3", new IncrementWidgetCommand(), expectedVersion: 5);

        await _session.Received(1).AppendAsync(
            "widgets/3",
            Arg.Any<IEnumerable<Event>>(),
            (long?)5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_returns_updated_state_after_commit()
    {
        var updatedWidget = new Widget(99) { Id = "widgets/4" };
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(updatedWidget));

        var dispatcher = BuildDispatcher();
        var result = await dispatcher.DispatchAsync<Widget>("widgets/4", new CreateWidgetCommand("widgets/4"));

        result.Should().Be(updatedWidget);
    }

    [Fact]
    public async Task DispatchAsync_propagates_handler_exception_without_calling_AppendAsync()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Widget?>(null));

        var dispatcher = new CommandDispatcher(_store, type =>
        {
            if (type == typeof(ICommandHandler<CreateWidgetCommand, Widget>))
                return new ThrowingHandler();
            throw new InvalidOperationException($"No handler for {type}");
        });

        var act = () => dispatcher.DispatchAsync<Widget>("widgets/x", new CreateWidgetCommand("widgets/x"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("rejected");

        await _session.DidNotReceive().AppendAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<IEvent>>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_propagates_exception_when_no_handler_is_registered()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Widget?>(null));

        var dispatcher = new CommandDispatcher(_store,
            _ => throw new InvalidOperationException("No handler registered"));

        var act = () => dispatcher.DispatchAsync<Widget>("widgets/x", new CreateWidgetCommand("widgets/x"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No handler registered");
    }

    [Fact]
    public async Task DispatchAsync_disposes_session_after_commit()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(new Widget(0) { Id = "widgets/5" }));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<Widget>("widgets/5", new CreateWidgetCommand("widgets/5"));

        await _session.Received(1).DisposeAsync();
    }
}
