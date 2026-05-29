using FluentAssertions;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Winche.Events.Abstractions;
using Winche.Events.Notification;
using Winche.Events.Session.Internal;
using Xunit;

namespace Winche.Events.Tests.Session;

record TestEvent : Event;

public class EventSessionNotifierTests
{
    private readonly IDocumentSession _martenSession = Substitute.For<IDocumentSession>();

    private EventSession BuildSession(params IAppendNotifier[] notifiers) =>
        new(_martenSession, Substitute.For<IDocumentStore>(), notifiers, NullLogger<EventSession>.Instance);

    [Fact]
    public async Task SaveChangesAsync_does_not_throw_when_a_notifier_fails()
    {
        var notifier = Substitute.For<IAppendNotifier>();
        notifier.NotifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("notifier blew up")));

        var session = BuildSession(notifier);
        await session.AppendStreamAsync("stream/1", [new TestEvent()]);

        var act = () => session.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveChangesAsync_calls_remaining_notifiers_after_one_fails()
    {
        var failing = Substitute.For<IAppendNotifier>();
        failing.NotifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException()));

        var succeeding = Substitute.For<IAppendNotifier>();

        var session = BuildSession(failing, succeeding);
        await session.AppendStreamAsync("stream/1", [new TestEvent()]);
        await session.SaveChangesAsync();

        await succeeding.Received(1).NotifyAsync("stream/1", Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChangesAsync_does_not_invoke_notifiers_when_nothing_was_appended()
    {
        var notifier = Substitute.For<IAppendNotifier>();

        var session = BuildSession(notifier);
        await session.SaveChangesAsync();

        await notifier.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChangesAsync_invokes_notifier_once_per_stream()
    {
        var notifier = Substitute.For<IAppendNotifier>();

        var session = BuildSession(notifier);
        await session.AppendStreamAsync("stream/1", [new TestEvent()]);
        await session.AppendStreamAsync("stream/2", [new TestEvent()]);
        await session.SaveChangesAsync();

        await notifier.Received(1).NotifyAsync("stream/1", Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).NotifyAsync("stream/2", Arg.Any<IReadOnlyList<IEvent>>(), Arg.Any<CancellationToken>());
    }
}
