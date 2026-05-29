namespace Winche.Events.Abstractions;

/// <summary>
/// Base interface for all commands. Associates a command with the aggregate type it targets,
/// enabling the dispatcher to infer the aggregate without explicit type arguments.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this command operates on.</typeparam>
public interface ICommand<TAggregate> where TAggregate : class, IAggregate 
{
    
}

/// <summary>
/// Base record for all commands.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this command operates on.</typeparam>
public abstract record Command<TAggregate> : ICommand<TAggregate> where TAggregate : class, IAggregate;
