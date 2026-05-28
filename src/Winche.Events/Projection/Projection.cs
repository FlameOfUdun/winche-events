using System.Collections.Concurrent;
using System.Reflection;

namespace Winche.Events.Projection;

/// <summary>
/// Base class for aggregate projections. Define typed <c>Apply</c> overloads — one per event type —
/// to fold events into <typeparamref name="TState"/>. Unhandled event types are silently ignored.
/// </summary>
/// <typeparam name="TState">The aggregate state type produced by this projection.</typeparam>
public abstract class Projection<TState> where TState : class
{
    private static readonly ConcurrentDictionary<(Type, Type), MethodInfo?> _methodCache = new();

    /// <summary>Returns the initial (empty) state used when a stream has no events yet.</summary>
    public abstract TState InitialState();

    internal TState ApplyEvent(TState state, object @event)
    {
        var key = (GetType(), @event.GetType());
        var method = _methodCache.GetOrAdd(key, k => k.Item1.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance, [typeof(TState), k.Item2]));

        if (method == null) return state;
        return (TState)method.Invoke(this, [state, @event])!;
    }
}
