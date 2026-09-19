using Tripwire.Tests.Support;
using Tripwire.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tripwire.Tests;

[Collection(ExitCodeCollection.Name)]
public class HostIntegrationTests
{
    private static IHost BuildHost(
        IServiceTrigger trigger,
        Func<CancellationToken, Task> body,
        FailureHandlingMode? failureHandling = null,
        OverlapPolicy? policy = null,
        ILogger? logger = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(_ => new TestService(trigger, body, policy, failureHandling, logger));
        return builder.Build();
    }

    [Fact]
    public async Task StopOnFailure_stops_the_host_with_a_non_zero_exit_code()
    {
        var trigger = new OnStartupTrigger();
        using var host = BuildHost(trigger, async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); }, FailureHandlingMode.StopOnFailure);

        Environment.ExitCode = 0;
        var run = host.RunAsync();
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(run, completed);
        Assert.Equal(1, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task StopOnFailure_stops_the_host_from_a_concurrent_iteration()
    {
        var trigger = new OnStartupTrigger();
        using var host = BuildHost(trigger, async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); }, FailureHandlingMode.StopOnFailure, OverlapPolicy.Concurrent(2));

        var run = host.RunAsync();
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(run, completed);
    }

    [Fact]
    public async Task Ignore_keeps_the_host_running()
    {
        var logger = new TestLogger();
        var trigger = new OnStartupTrigger();
        using var host = BuildHost(trigger, async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); }, FailureHandlingMode.Ignore, logger: logger);

        var run = host.RunAsync();
        await Wait.UntilAsync(() => logger.Errors.Any());
        await Wait.SettleAsync();

        Assert.False(run.IsCompleted);

        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await run;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Notified_service_runs_after_its_producer_whatever_the_registration_order(bool producerFirst)
    {
        var order = new List<string>();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Notifier<TestService>>();

        void AddProducer() => builder.Services.AddSingleton<IHostedService>(sp => new TestService(
            new OnStartupTrigger(),
            _ =>
            {
                lock (order) order.Add("producer");
                sp.GetRequiredService<Notifier<TestService>>().Notify();
                return Task.CompletedTask;
            }));

        void AddConsumer() => builder.Services.AddSingleton<IHostedService>(sp => new TestService(
            new NotificationTrigger(sp.GetRequiredService<Notifier<TestService>>()),
            _ =>
            {
                lock (order) order.Add("consumer");
                return Task.CompletedTask;
            }));

        if (producerFirst)
        {
            AddProducer();
            AddConsumer();
        }
        else
        {
            AddConsumer();
            AddProducer();
        }

        using var host = builder.Build();
        await host.StartAsync();
        await Wait.UntilAsync(() => { lock (order) return order.Count == 2; });
        await host.StopAsync();

        Assert.Equal(["producer", "consumer"], order);
    }

    [Fact]
    public async Task Startup_then_periodic_composite_runs_immediately_and_then_on_schedule()
    {
        var calls = 0;
        var trigger = new CompositeTrigger(
            new OnStartupTrigger(),
            new PeriodicTrigger(TimeSpan.FromMilliseconds(20), TimeProvider.System));

        using var host = BuildHost(trigger, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });

        await host.StartAsync();
        await Wait.UntilAsync(() => calls >= 3);
        await host.StopAsync();
    }
}
