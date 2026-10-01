using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tripwire.Triggers;

namespace Tripwire.AspNetCore.Tests;

public class MapTriggerTests
{
    /// <summary>
    /// Service listening to its <see cref="Notifier{TService}"/>, whose iteration body is supplied by the test.
    /// </summary>
    private sealed class ListeningService(Notifier<ListeningService> notifier, Func<CancellationToken, Task> body)
        : TriggeredHostedService(new NotificationTrigger(notifier))
    {
        protected override Task ExecuteIteration(CancellationToken cancellationToken) => body(cancellationToken);
    }

    /// <summary>
    /// A web application with an in-memory server, a notifier and, optionally, a service listening to it.
    /// </summary>
    private static WebApplication BuildApp(Func<CancellationToken, Task>? body = null, bool registerNotifier = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        if (registerNotifier)
        {
            builder.Services.AddSingleton<Notifier<ListeningService>>();
        }

        if (body is not null)
        {
            builder.Services.AddSingleton<IHostedService>(sp => new ListeningService(sp.GetRequiredService<Notifier<ListeningService>>(), body));
        }

        return builder.Build();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > 2000)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        using var app = BuildApp();

        Assert.Equal("endpoints", Assert.Throws<ArgumentNullException>(() => ((IEndpointRouteBuilder)null!).MapTrigger<ListeningService>("/run")).ParamName);
        Assert.Equal("pattern", Assert.Throws<ArgumentNullException>(() => app.MapTrigger<ListeningService>(null!)).ParamName);
    }

    [Fact]
    public void Fails_at_startup_when_the_notifier_is_not_registered()
    {
        using var app = BuildApp(registerNotifier: false);

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapTrigger<ListeningService>("/run"));

        Assert.Contains("Notifier<ListeningService>", ex.Message);
    }

    [Fact]
    public async Task Post_runs_the_service_and_responds_accepted()
    {
        var calls = 0;
        using var app = BuildApp(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        app.MapTrigger<ListeningService>("/jobs/run");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("/jobs/run", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await UntilAsync(() => calls == 1);

        await client.PostAsync("/jobs/run", null);
        await UntilAsync(() => calls == 2);

        await app.StopAsync();
    }

    [Fact]
    public async Task Only_post_is_allowed()
    {
        var calls = 0;
        using var app = BuildApp(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        app.MapTrigger<ListeningService>("/jobs/run");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/jobs/run");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await Task.Delay(50);
        Assert.Equal(0, calls);

        await app.StopAsync();
    }

    [Fact]
    public async Task Endpoint_accepts_conventions()
    {
        using var app = BuildApp();
        app.MapTrigger<ListeningService>("/jobs/run").WithName("run-job");

        await app.StartAsync();

        var mapped = Assert.Single(app.Services.GetRequiredService<EndpointDataSource>().Endpoints);
        Assert.Equal("Trigger ListeningService", mapped.DisplayName);
        Assert.Equal("run-job", mapped.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);

        await app.StopAsync();
    }
}
