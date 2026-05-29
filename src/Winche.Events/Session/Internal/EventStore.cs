using System.Data;
using Marten;
using Marten.Services;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;

namespace Winche.Events.Session.Internal;

internal sealed class EventStore : IEventStore
{
    private readonly IDocumentStore _martenStore;
    private readonly IReadOnlyList<IAppendNotifier> _notifiers;
    private readonly ILogger<EventSession> _logger;

    internal EventStore(
        IDocumentStore martenStore,
        IReadOnlyList<IAppendNotifier> notifiers,
        ILogger<EventSession> logger)
    {
        _martenStore = martenStore;
        _notifiers = notifiers;
        _logger = logger;
    }

    public Task<IEventSession> OpenSessionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        var martenSession = _martenStore.OpenSession(new SessionOptions
        {
            IsolationLevel = isolationLevel,
        });
        return Task.FromResult<IEventSession>(new EventSession(martenSession, _martenStore, _notifiers, _logger));
    }
}
