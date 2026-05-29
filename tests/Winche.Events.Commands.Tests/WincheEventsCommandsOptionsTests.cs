using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Commands.DependencyInjection;
using Xunit;

namespace Winche.Events.Commands.Tests;

public class WincheEventsCommandsOptionsTests
{
    [Fact]
    public void AddCommandHandler_registers_handler_as_singleton_in_DI()
    {
        var services = new ServiceCollection();
        var options = new WincheEventsCommandsOptions();

        options.AddCommandHandler<ThingCommandHandler, Thing>();

        foreach (var reg in options.Registrations)
            reg(services);

        var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<CommandHandler<Thing>>();
        handler.Should().BeOfType<ThingCommandHandler>();
    }

    [Fact]
    public void AddCommandHandler_stores_one_registration_per_call()
    {
        var options = new WincheEventsCommandsOptions();

        options.AddCommandHandler<ThingCommandHandler, Thing>();

        options.Registrations.Should().HaveCount(1);
    }
}
