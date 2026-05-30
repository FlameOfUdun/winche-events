namespace Winche.Events.Abstractions;

/// <summary>
/// Base interface for all commands. Associates a command with the aggregate type it targets,
/// enabling the dispatcher to infer the aggregate without explicit type arguments.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this command operates on.</typeparam>
public interface ICommand<TAggregate> where TAggregate : class, IAggregate
{
    /// <summary>
    /// The stream version the issuer observed when this command was created.
    /// The dispatcher passes this to <c>AppendStreamAsync</c> so the server rejects the command
    /// if the stream has advanced since the command was issued.
    /// <c>null</c> skips the version check (safe for server-originated commands).
    /// </summary>
    long? ExpectedVersion => null;

    /// <summary>
    /// When this command was created (UTC). Populated automatically by <see cref="Command{TAggregate}"/>.
    /// Useful for audit trails and offline-sync conflict detection.
    /// </summary>
    DateTimeOffset CreatedAt => DateTimeOffset.UtcNow;
}

/// <summary>
/// Base record for all commands.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type this command operates on.</typeparam>
public abstract record Command<TAggregate> : ICommand<TAggregate> where TAggregate : class, IAggregate
{
    /// <inheritdoc/>
    public long? ExpectedVersion { get; init; }

    /// <inheritdoc/>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
