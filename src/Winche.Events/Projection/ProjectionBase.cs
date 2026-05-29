using Marten;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Events.Projection;

/// <summary>
/// Non-generic base for all projections. Inherit from <see cref="Projection{TAggregate}"/> — do not inherit directly.
/// </summary>
public abstract class ProjectionBase
{
    internal abstract Type AggregateType { get; }
    internal abstract void RegisterServices(IServiceCollection services);
    internal abstract void ConfigureMarten(StoreOptions storeOptions, IServiceProvider sp, ProjectionLifecycle lifecycle);
}
