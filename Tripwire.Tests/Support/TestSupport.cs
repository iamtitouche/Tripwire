using System.Collections.Concurrent;
using System.Diagnostics;

namespace Tripwire.Tests.Support;

/// <summary>
/// Wraps a trigger and signals when it has been started, i.e. once it is waiting on its time source.
/// <c>BackgroundService.StartAsync</c> may return before <c>ExecuteAsync</c> has reached the trigger,
/// so a test must not advance a <see cref="FakeTimeProvider"/> before this signal.
/// </summary>
internal sealed class SignallingTrigger(IServiceTrigger inner) : IServiceTrigger
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        // The inner trigger runs synchronously up to its first await: its timer exists when this returns.
        var run = inner.StartAsync(execute, cancellationToken);
        _started.TrySetResult();
        return run;
    }
}

/// <summary>
/// Test service whose iteration body is supplied by the test.
/// </summary>
internal sealed class TestService(
    IServiceTrigger trigger,
    Func<CancellationToken, Task> body,
    OverlapPolicy? overlapPolicy = null,
    FailureHandlingMode? failureHandling = null,
    ILogger? logger = null,
    TimeProvider? timeProvider = null)
    : TriggeredHostedService(trigger, overlapPolicy, failureHandling, logger, timeProvider)
{
    protected override Task ExecuteIteration(CancellationToken cancellationToken)
    {
        return body(cancellationToken);
    }
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Logger that captures entries for assertions.
/// </summary>
internal sealed class TestLogger : ILogger
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IEnumerable<LogEntry> Errors => Entries.Where(e => e.Level == LogLevel.Error);

    public IEnumerable<LogEntry> Warnings => Entries.Where(e => e.Level == LogLevel.Warning);

    public IEnumerable<LogEntry> Informations => Entries.Where(e => e.Level == LogLevel.Information);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal static class Wait
{
    /// <summary>
    /// Waits until a condition becomes true, yielding so that continuations get to run.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Gives continuations a short moment to run, so the caller can check that nothing else happened.
    /// </summary>
    public static async Task SettleAsync(int ms = 50)
    {
        await Task.Delay(ms);
    }
}

/// <summary>
/// Tests that read or write <see cref="Environment.ExitCode"/>, which is process-wide, must not run in
/// parallel with each other: this collection serializes their classes.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ExitCodeCollection
{
    public const string Name = "Environment.ExitCode";
}

/// <summary>
/// Time zones built by hand, so the tests do not depend on the machine's time zone database.
/// </summary>
internal static class TestTimeZones
{
    /// <summary>
    /// UTC+1 with European-style daylight saving (last Sunday of March 02:00 → last Sunday of October 03:00).
    /// </summary>
    public static readonly TimeZoneInfo CentralEurope = TimeZoneInfo.CreateCustomTimeZone(
        "Test/CentralEurope",
        TimeSpan.FromHours(1),
        "Central Europe (test)",
        "CET",
        "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    public static readonly TimeZoneInfo UtcPlus5 = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+5", TimeSpan.FromHours(5), "UTC+5", "UTC+5");
}

/// <summary>
/// Results for the <c>execute</c> callback handed to a trigger under test.
/// </summary>
internal static class TestFiring
{
    /// <summary>
    /// An accepted firing that is already over.
    /// </summary>
    public static Task<Firing> Accepted { get; } = Task.FromResult(new Firing(Task.CompletedTask));
}
