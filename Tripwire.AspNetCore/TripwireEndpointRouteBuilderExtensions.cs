using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tripwire;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps HTTP endpoints that trigger a <see cref="TriggeredHostedService"/>.
/// </summary>
public static class TripwireEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a <c>POST</c> endpoint that requests one run of <typeparamref name="TService"/> through its
    /// <see cref="Notifier{TService}"/>, and responds <c>202 Accepted</c> at once: the run itself is handled by the
    /// service's <see cref="OverlapPolicy"/> like any other firing. The service must listen to the notifier with
    /// a <see cref="Tripwire.Triggers.NotificationTrigger"/>, and the notifier must be registered as a singleton.
    /// </summary>
    /// <typeparam name="TService">The service to trigger, as used to tag its <see cref="Notifier{TService}"/>.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern, e.g. <c>/jobs/enrich</c>.</param>
    /// <returns>The endpoint, to add conventions such as <c>RequireAuthorization()</c>.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="Notifier{TService}"/> is registered.</exception>
    public static RouteHandlerBuilder MapTrigger<TService>(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);

        // Resolved once, at startup, so a missing registration fails the application rather than the first request.
        var notifier = endpoints.ServiceProvider.GetService<Notifier<TService>>()
            ?? throw new InvalidOperationException(
                $"No Notifier<{typeof(TService).Name}> is registered. Add services.AddSingleton<Notifier<{typeof(TService).Name}>>() " +
                $"and have {typeof(TService).Name} listen to it with a NotificationTrigger.");

        return endpoints
            .MapPost(pattern, () =>
            {
                notifier.Notify();
                return Results.Accepted();
            })
            .WithDisplayName($"Trigger {typeof(TService).Name}");
    }
}
