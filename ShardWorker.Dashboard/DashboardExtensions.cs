using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ShardWorker.Core.Model;

namespace ShardWorker.Dashboard;

public static class DashboardExtensions
{
    /// <summary>
    /// Registers the <see cref="ShardDashboardService"/> and configures Razor component
    /// rendering required by <see cref="MapShardWorkerDashboard"/>.
    /// Call this from <c>builder.Services</c> before building the host.
    /// </summary>
    public static IServiceCollection AddShardWorkerDashboard(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddTransient<ShardDashboardService>();
        return services;
    }

    /// <summary>
    /// Maps a GET endpoint at <paramref name="path"/> that renders the ShardWorker dashboard.
    /// Call this after <c>app.Build()</c>.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="path">URL path for the dashboard. Defaults to <c>/shard-dashboard</c>.</param>
    /// <param name="refreshIntervalMs">
    /// How often the dashboard page auto-reloads, in milliseconds. Defaults to 2000 ms.
    /// Reduce this when workers hold locks for short periods (e.g. 500–1000 ms).
    /// </param>
    public static IEndpointRouteBuilder MapShardWorkerDashboard(
        this IEndpointRouteBuilder endpoints,
        string path = "/shard-dashboard",
        int refreshIntervalMs = 2000)
    {
        endpoints.MapGet(path, () => new RazorComponentResult<Components.ShardDashboard>(
            new Dictionary<string, object?> { [nameof(Components.ShardDashboard.RefreshIntervalMs)] = refreshIntervalMs }))
                 .ExcludeFromDescription();
        return endpoints;
    }
}
