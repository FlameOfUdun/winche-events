using System.Data;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Events.Notification;

namespace Winche.Events.Session.Internal;

internal sealed class EventStore : IEventStore
{
    private readonly IDocumentStore _martenStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlySet<Type> _inlineProjectionTypes;
    private readonly IReadOnlyList<IAppendNotifier> _notifiers;
    private readonly ILogger<EventSession> _logger;

    internal EventStore(
        IDocumentStore martenStore,
        IServiceProvider serviceProvider,
        IReadOnlySet<Type> inlineProjectionTypes,
        IReadOnlyList<IAppendNotifier> notifiers,
        ILogger<EventSession> logger)
    {
        _martenStore = martenStore;
        _serviceProvider = serviceProvider;
        _inlineProjectionTypes = inlineProjectionTypes;
        _notifiers = notifiers;
        _logger = logger;
    }

    public Task<IEventSession> OpenSessionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        // SessionOptions moved to Marten.Services in Marten 9.x
        var martenSession = _martenStore.OpenSession(new Marten.Services.SessionOptions
        {
            IsolationLevel = isolationLevel,
        });

        return Task.FromResult<IEventSession>(new EventSession(
            martenSession,
            _serviceProvider,
            _inlineProjectionTypes,
            _notifiers,
            _logger));
    }
}
