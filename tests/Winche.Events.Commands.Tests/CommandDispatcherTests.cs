using FluentAssertions;
using NSubstitute;
using Winche.Events.Commands;
using Winche.Events.Commands.Internal;
using Winche.Events.Models;
using Winche.Events.Session;
using Xunit;

namespace Winche.Events.Commands.Tests;

record Widget(string Id, int Count) : DomainEvent;
record WidgetCreated(string Id) : DomainEvent;
record WidgetIncremented : DomainEvent;

class CreateWidgetCommand(string Id) { public string Id { get; } = Id; }
class IncrementWidgetCommand;

class CreateWidgetHandler : ICommandHandler<CreateWidgetCommand, Widget>
{
    public Task<IEnumerable<DomainEvent>> HandleAsync(CreateWidgetCommand cmd, Widget? state, CancellationToken ct)
    {
        if (state != null) throw new InvalidOperationException("Widget already exists.");
        return Task.FromResult<IEnumerable<DomainEvent>>([new WidgetCreated(cmd.Id)]);
    }
}

class IncrementWidgetHandler : ICommandHandler<IncrementWidgetCommand, Widget>
{
    public Task<IEnumerable<DomainEvent>> HandleAsync(IncrementWidgetCommand cmd, Widget? state, CancellationToken ct)
    {
        if (state == null) return Task.FromResult(Enumerable.Empty<DomainEvent>());
        return Task.FromResult<IEnumerable<DomainEvent>>([new WidgetIncremented()]);
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
        // First call returns null (pre-append), second returns the created widget (post-commit)
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(new Widget("widgets/1", 0)));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<CreateWidgetCommand, Widget>("widgets/1", new CreateWidgetCommand("widgets/1"));

        await _session.Received(1).AppendAsync<Widget>(
            "widgets/1",
            Arg.Is<IEnumerable<DomainEvent>>(e => e.OfType<WidgetCreated>().Any()),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_calls_SaveChangesAsync_after_append()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(new Widget("widgets/2", 0)));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<CreateWidgetCommand, Widget>("widgets/2", new CreateWidgetCommand("widgets/2"));

        await _session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_passes_expectedVersion_to_AppendAsync()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(new Widget("widgets/3", 1)),
                Task.FromResult<Widget?>(new Widget("widgets/3", 2)));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<IncrementWidgetCommand, Widget>("widgets/3", new IncrementWidgetCommand(), expectedVersion: 5);

        await _session.Received(1).AppendAsync<Widget>(
            "widgets/3",
            Arg.Any<IEnumerable<DomainEvent>>(),
            (long?)5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_returns_updated_state_after_commit()
    {
        var updatedWidget = new Widget("widgets/4", 99);
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(updatedWidget));

        var dispatcher = BuildDispatcher();
        var result = await dispatcher.DispatchAsync<CreateWidgetCommand, Widget>("widgets/4", new CreateWidgetCommand("widgets/4"));

        result.Should().Be(updatedWidget);
    }

    [Fact]
    public async Task DispatchAsync_disposes_session_after_commit()
    {
        _session.LoadAsync<Widget>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<Widget?>(null),
                Task.FromResult<Widget?>(new Widget("widgets/5", 0)));

        var dispatcher = BuildDispatcher();
        await dispatcher.DispatchAsync<CreateWidgetCommand, Widget>("widgets/5", new CreateWidgetCommand("widgets/5"));

        await _session.Received(1).DisposeAsync();
    }
}
