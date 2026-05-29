using FluentAssertions;
using Winche.Events.Commands.DependencyInjection;
using Xunit;

namespace Winche.Events.Commands.Tests;

class NotAHandler { }

public class WincheEventsCommandsOptionsTests
{
    [Fact]
    public void AddHandler_throws_when_type_does_not_implement_ICommandHandler()
    {
        var options = new WincheEventsCommandsOptions();

        var act = () => options.AddHandler<NotAHandler>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must implement ICommandHandler*");
    }
}
