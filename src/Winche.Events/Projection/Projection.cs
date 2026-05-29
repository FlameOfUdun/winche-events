using System.Collections.Concurrent;
using System.Reflection;
using Marten;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Winche.Events.Abstractions;
using Winche.Events.Projection.Internal;

namespace Winche.Events.Projection;

/// <summary>
/// Base class for aggregate projections. Define typed <c>Apply</c> or <c>ApplyAsync</c> overloads —
/// one per event type — to fold events into <typeparamref name="TAggregate"/>. Unhandled event types are
/// silently ignored.
/// </summary>
/// <typeparam name="TAggregate">The aggregate state type produced by this projection.</typeparam>
public abstract class Projection<TAggregate> : ProjectionBase where TAggregate : class, IAggregate
{
    private static readonly ConcurrentDictionary<(Type, Type), MethodInfo?> _syncMethodCache = new();
    private static readonly ConcurrentDictionary<(Type, Type), MethodInfo?> _asyncMethodCache = new();

    /// <summary>Returns the initial (empty) state for the given stream identifier.</summary>
    public abstract TAggregate Create(string id);

    internal override sealed Type AggregateType => typeof(TAggregate);

    internal override sealed void RegisterServices(IServiceCollection services)
        => services.AddSingleton(typeof(Projection<TAggregate>), GetType());

    internal override sealed void ConfigureMarten(StoreOptions storeOptions, IServiceProvider sp, ProjectionLifecycle lifecycle)
    {
        var projection = (Projection<TAggregate>)sp.GetRequiredService(typeof(Projection<TAggregate>));
        var bridge = new ProjectionBridge<TAggregate>(projection);
        storeOptions.Projections.AddGlobalProjection<TAggregate, string>(bridge, lifecycle);
    }

    internal TAggregate ApplyEvent(TAggregate state, object @event)
    {
        var key = (GetType(), @event.GetType());
        var method = _syncMethodCache.GetOrAdd(key, k => k.Item1.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance, [typeof(TAggregate), k.Item2]));

        if (method == null) return state;
        return (TAggregate)method.Invoke(this, [state, @event])!;
    }

    internal async Task<TAggregate> ApplyEventAsync(TAggregate state, object @event)
    {
        var key = (GetType(), @event.GetType());
        var asyncMethod = _asyncMethodCache.GetOrAdd(key, k => k.Item1.GetMethod("ApplyAsync", BindingFlags.Public | BindingFlags.Instance, [typeof(TAggregate), k.Item2]));

        if (asyncMethod != null)
            return await (Task<TAggregate>)asyncMethod.Invoke(this, [state, @event])!;

        return ApplyEvent(state, @event);
    }
}
