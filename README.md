# Tripwire

[![NuGet](https://img.shields.io/nuget/v/Tripwire.svg)](https://www.nuget.org/packages/Tripwire)
[![Downloads](https://img.shields.io/nuget/dt/Tripwire.svg)](https://www.nuget.org/packages/Tripwire)
[![CI](https://github.com/iamtitouche/Tripwire/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/iamtitouche/Tripwire/actions/workflows/ci.yml)

A small trigger ecosystem for .NET hosted services. Write your background job once as a
`TriggeredHostedService`, then pick *when* it runs by plugging in a trigger: on startup,
at a fixed interval, at given times of day, on a cron expression, when another service says so — or
any combination of these.

Built on `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`
only — it works in any generic host
(console apps, workers, ASP.NET Core) and targets .NET 8, 9 and 10.

## Installation

```shell
dotnet add package Tripwire
```

## Usage

Derive from `TriggeredHostedService`, implement `ExecuteIteration`, and hand a trigger to the
base constructor:

```csharp
using Tripwire;
using Tripwire.Triggers;

public class CleanupService(ILogger<CleanupService> logger)
    : TriggeredHostedService(new PeriodicTrigger(TimeSpan.FromMinutes(5)), logger: logger)
{
    protected override Task ExecuteIteration(CancellationToken cancellationToken)
    {
        // your work here
        return Task.CompletedTask;
    }
}
```

Triggers compose: to run immediately *and* every 5 minutes, wrap several triggers in a
`CompositeTrigger`:

```csharp
new CompositeTrigger(
    new OnStartupTrigger(),
    new PeriodicTrigger(TimeSpan.FromMinutes(5)))
```

Then register it like any hosted service:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CleanupService>();
await builder.Build().RunAsync();
```

How iterations behave when the trigger fires while one is still running is controlled by the
`OverlapPolicy` passed to the base constructor. A policy is two numbers: how many iterations may run
at the same time, and how many firings may wait for a free slot. A firing that lands when all slots
are busy is queued; when the queue is full, it is deliberately dropped. Queued firings run in order.

| Policy | Concurrency | Queue | Behavior |
|---|---|---|---|
| `OverlapPolicy.Wait` (default) | 1 | unbounded | Firings wait their turn. Iterations never overlap and no firing is lost. Each firing that has to wait is logged as a **warning** with the queue depth: the service cannot keep up with its trigger. |
| `OverlapPolicy.Skip` | 1 | 0 | A firing that lands during an iteration is dropped, with an information entry. Best for idempotent jobs (cleanup, sync, health checks) where replaying a late tick has no value. |
| `OverlapPolicy.Concurrent(max)` | `max` | unbounded | Up to `max` iterations run in parallel; further firings wait for a slot, with the same warning as `Wait`. In-flight iterations are awaited on shutdown. |
| `policy.WithMaxPending(n)` | as is | `n` | Bounds the queue of any policy. Queued firings are logged as information, and a firing that lands when `n` are already waiting is dropped with a **warning**. `WithMaxPending(1)` coalesces a burst of firings into a single catch-up run. |

```csharp
public class SyncService(ILogger<SyncService> logger)
    : TriggeredHostedService(
        new PeriodicTrigger(TimeSpan.FromMinutes(1)),
        OverlapPolicy.Skip,
        logger: logger)
{
    // ...
}
```

```csharp
// at most 4 in parallel, at most 10 waiting; anything beyond is dropped with a warning
OverlapPolicy.Concurrent(4).WithMaxPending(10)

// one at a time, and a burst of firings collapses into a single pending run
OverlapPolicy.Wait.WithMaxPending(1)
```

Triggers never wait for an iteration: a firing is handed to the service and the trigger goes back to
watching the clock. A `PeriodicTrigger` with a 1-minute interval and 3-minute iterations therefore
fires every minute whatever the policy — under `Wait` the ticks queue up (and the log says so), under
`Skip` they are dropped, under `Concurrent(3)` three iterations run at once. Queued firings that have
not started when the host shuts down are abandoned; running iterations are awaited. The one trigger
that deliberately waits is `FixedDelayTrigger`, which paces itself on the end of each iteration.

## Triggers

### `OnStartupTrigger`

Runs the service once, as soon as the host starts it.

```csharp
new OnStartupTrigger()
```

### `PeriodicTrigger`

Runs the service at a fixed rate: every interval, however long the iterations take. The first run
happens one interval after startup.

```csharp
new PeriodicTrigger(TimeSpan.FromMinutes(5))
```

### `FixedDelayTrigger`

Runs the service with a fixed delay between the **end** of an iteration and the start of the next: a
slow iteration pushes the next one back instead of piling firings up, so the trigger's own runs never
overlap each other. The first run happens after `initialDelay`, which defaults to the delay; pass
`TimeSpan.Zero` to run right away.

```csharp
// right away, then 30 seconds after the end of each run
new FixedDelayTrigger(TimeSpan.FromSeconds(30), initialDelay: TimeSpan.Zero)
```

With a 1-minute interval and 3-minute iterations, a `PeriodicTrigger` fires every minute and leaves
the extra firings to the `OverlapPolicy`; a `FixedDelayTrigger` with a 1-minute delay starts a run every
4 minutes and, on its own, never queues anything. In a `CompositeTrigger` that guarantee only covers its
own runs: the other triggers' firings are still handled by the `OverlapPolicy` (queued behind its run,
or run alongside it with `Concurrent`), and its own firing may wait behind another trigger's run, which
pushes its next start back. The delay also follows a failed iteration; retries from
`FailureHandlingMode.Retry` are separate firings that neither wait for it nor reset it.

### `ScheduleTrigger`

Runs the service every day at the given times. Times are interpreted in UTC by default, or in the
`TimeZoneInfo` you provide.

```csharp
// 08:00 and 20:00 UTC
new ScheduleTrigger([new TimeOnly(8, 0), new TimeOnly(20, 0)])

// 08:00 and 20:00 Paris time, daylight saving handled
new ScheduleTrigger(
    [new TimeOnly(8, 0), new TimeOnly(20, 0)],
    timeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"))
```

Daylight saving transitions are handled: a scheduled time that does not exist on a given day
(spring forward) is skipped for that day, and a time that occurs twice (fall back) runs once, at its
first instance, as with `CronTrigger`.

`ScheduleTrigger` is really a loop over an `ISchedule`, the object that computes the next occurrence;
the constructor above is shorthand for a `DailySchedule`. See [Writing your own schedule](#writing-your-own-schedule)
for the general form.

### `CronTrigger`

Runs the service on a standard cron expression, interpreted in UTC by default, or in the
`TimeZoneInfo` you provide.

```csharp
// weekdays at 08:00 UTC
new CronTrigger("0 8 * * MON-FRI")

// every 15 minutes during office hours, Paris time
new CronTrigger("*/15 9-17 * * *", timeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"))

// first day of the month at midnight
new CronTrigger("@monthly")
```

The expression has five fields, `minute hour day-of-month month day-of-week`, with the usual syntax:

| Syntax | Meaning | Example |
|---|---|---|
| `*` | every value | `* * * * *` — every minute |
| `a,b,c` | list | `0 8,12,18 * * *` — at 08:00, 12:00 and 18:00 |
| `a-b` | range | `0 9-17 * * *` — every hour from 09:00 to 17:00 |
| `*/n`, `a-b/n`, `a/n` | step | `*/15 * * * *` — every 15 minutes; `5/10 * * * *` — at :05, :15, :25… |
| `JAN`–`DEC`, `SUN`–`SAT` | names, case-insensitive | `0 0 1 JAN *`, `0 8 * * mon-fri` |
| `0` or `7` | Sunday | `0 0 * * 7` |
| `@yearly` `@annually` `@monthly` `@weekly` `@daily` `@midnight` `@hourly` | macros | `@daily` — every day at 00:00 |

When both the day of month and the day of week are restricted (do not start with `*`), a day
matches if *either* does, as in Vixie cron: `0 0 13 * FRI` runs on the 13th of every month *and* on
every Friday. An expression that never matches, such as `0 0 31 2 *`, completes the trigger without
ever firing.

Daylight saving transitions are handled:

- A wall-clock time that does not exist (spring forward) is skipped, and an interval such as
  `*/15 * * * *` simply continues on the other side of the gap.
- A wall-clock time that occurs twice (fall back) runs **once**, at its first instance, when the hour
  field is fixed (`30 2 * * *`), and in **both** instances when the hour field is an interval
  (`*/15 * * * *`, `0 * * * *`), so that the interval is kept.

An invalid expression throws an `ArgumentException` naming the field and the offending token.

### `NotificationTrigger`

Runs the service each time a `Notifier` is notified — typically by another service that has just
produced the data this one enriches, or simply to guarantee that one service runs after another.
The notifier is shared through dependency injection; `Notifier<TTag>` tags it with a type so that
several can be registered without keys.

In a pipeline, tag the notifier with the **event** the producer emits, not with either service: the
producer notifies `Notifier<BatchImported>`, the consumer listens to it, and neither names the other.
The dependency runs downstream only — the consumer depends on the producer's contract, the producer
knows nothing of its consumers.

```csharp
// the producer's contract: "a batch has been imported" — used as a tag only
public sealed class BatchImported;

// producer: notifies once its work is done, without knowing who listens
public class ImportService(Notifier<BatchImported> batchImported, ILogger<ImportService> logger)
    : TriggeredHostedService(new PeriodicTrigger(TimeSpan.FromMinutes(10)), logger: logger)
{
    protected override async Task ExecuteIteration(CancellationToken cancellationToken)
    {
        await ImportAsync(cancellationToken);
        batchImported.Notify();
    }
}

// consumer: runs after each import, without knowing who imports
public class EnrichmentService(Notifier<BatchImported> batchImported, ILogger<EnrichmentService> logger)
    : TriggeredHostedService(new NotificationTrigger(batchImported), logger: logger)
{
    protected override Task ExecuteIteration(CancellationToken cancellationToken) => EnrichAsync(cancellationToken);
}

builder.Services.AddSingleton<Notifier<BatchImported>>();
builder.Services.AddHostedService<ImportService>();
builder.Services.AddHostedService<EnrichmentService>();
```

`Notify()` never blocks and never loses a notification: one sent before the listening service has
started is kept and fires as soon as it does, so the order of registration does not matter. Each
notification is a firing like any other, handled by the service's `OverlapPolicy`: with
`Wait.WithMaxPending(1)` a burst of notifications during an iteration collapses into a single
catch-up run. A notifier serves one trigger; to fan out to several services, give each its own
notifier and notify them all.

`Notify()` can of course be called from anywhere — an endpoint, a message handler — which makes
`NotificationTrigger` the on-demand trigger too. There the caller means "run this service", so the
notifier is tagged with the **service** instead: `Notifier<EnrichmentService>`.

#### Triggering over HTTP

In ASP.NET Core, an endpoint that runs a service is a one-liner over its notifier:

```csharp
builder.Services.AddSingleton<Notifier<EnrichmentService>>();
builder.Services.AddHostedService<EnrichmentService>();

var app = builder.Build();

// POST /jobs/enrich → 202 Accepted, and EnrichmentService runs
app.MapPost("/jobs/enrich", (Notifier<EnrichmentService> notifier) =>
    {
        notifier.Notify();
        return Results.Accepted();
    })
    .RequireAuthorization("operators");
```

The endpoint responds at once: the run is handled by the service's `OverlapPolicy` like any other
firing, so hammering the endpoint during an iteration queues, coalesces or drops according to the
policy. Bound the queue (`OverlapPolicy.Wait.WithMaxPending(1)`) so that requests cannot pile up
runs, and protect the endpoint as you would any other.

### `CompositeTrigger`

Combines several triggers: the service runs whenever any of them fires. The inner triggers are
started in parallel and share the service's `OverlapPolicy` and `FailureHandlingMode`, so with
`Wait` (the default) their firings are serialized, and with `Skip` a firing that lands during
another trigger's iteration is dropped.

```csharp
// once at startup, then every day at 02:00 UTC
new CompositeTrigger(
    new OnStartupTrigger(),
    new ScheduleTrigger([new TimeOnly(2, 0)]))
```

If one of the inner triggers fails, the others are stopped and the exception is propagated, just
like a single failing trigger would.

## Error handling

What happens when `ExecuteIteration` throws is controlled by the `FailureHandlingMode` passed to
`TriggeredHostedService`. It applies to every iteration of the service, whichever trigger started it.

| Mode | Behavior |
|---|---|
| `FailureHandlingMode.Ignore` (default) | The exception is logged through the service's `ILogger` and the iteration is abandoned. The service keeps running and will fire again on the next occurrence. |
| `FailureHandlingMode.StopOnFailure` | The exception is logged, `Environment.ExitCode` is set to 1 and the exception is propagated out of the hosted service. The host logs it as critical and stops the application gracefully (the default `BackgroundServiceExceptionBehavior.StopHost`), and the process reports a failure. Use `StopOnFailureWithExitCode(code)` for a custom non-zero exit code. |
| `FailureHandlingMode.Retry(...)` | The service schedules a new iteration after a delay. See below. |

```csharp
// critical: any failure stops the application
public class OutboxRelay(ILogger<OutboxRelay> logger)
    : TriggeredHostedService(
        new PeriodicTrigger(TimeSpan.FromSeconds(10)),
        failureHandling: FailureHandlingMode.StopOnFailure,
        logger: logger)
{
    // ...
}

// transient failures: up to 5 attempts, 30 s apart
public class ReportUploader(ILogger<ReportUploader> logger)
    : TriggeredHostedService(
        new ScheduleTrigger([new TimeOnly(6, 0)]),
        failureHandling: FailureHandlingMode.Retry(maxAttempts: 5, delay: TimeSpan.FromSeconds(30), backoffFactor: 1),
        logger: logger)
{
    // ...
}
```

### Stop on failure

With `StopOnFailure`, the trigger and the service's other in-flight iterations are cancelled, then the
host stops gracefully — `StopAsync` runs on every hosted service — but the process exits with a
non-zero code, so `systemd Restart=on-failure`, container orchestrators and scripts see the failure.

If you configured `HostOptions.BackgroundServiceExceptionBehavior = Ignore`, `StopOnFailure` only
logs the exception, sets the exit code and the application keeps running without the service.

### Retry

`Retry(maxAttempts, delay, backoffFactor, maxDelay, allowRetryAgainAfter)` defaults to 3 attempts,
a 1-second initial delay, a backoff factor of 2 and a 5-minute cap, so the retries happen 1 s and
then 2 s after each failure. The delay grows geometrically (1 s, 2 s, 4 s, 8 s…) until it reaches
`maxDelay`, then stays there. Pass `backoffFactor: 1` for a constant delay.

Retries are not attached to one firing but form a **retry cycle shared by the whole service**:

- The first failure opens a cycle and schedules a retry. The retry is dispatched like any other
  firing, so it obeys the service's `OverlapPolicy` — with `Concurrent`, a retry and a regular
  firing can run at the same time; with `Skip` or a full queue, it is dropped like any other firing.
- **Any successful iteration closes the cycle**, whichever trigger started it, and cancels the
  pending retry, even one already waiting for a slot. A failed startup run that is followed by a
  successful periodic run is considered recovered.
- **Any failed iteration counts as an attempt** and restarts the wait from zero with the delay of
  the new attempt. `maxAttempts` counts every attempt of the cycle, the first run included.
- Once `maxAttempts` is reached, the cycle is exhausted: further failures are logged as errors
  without retrying, until a success closes the cycle or `allowRetryAgainAfter` has elapsed since
  the **first failure of the cycle**. It defaults to zero, meaning the next failure after exhaustion
  opens a new cycle.

For example, with a 30-minute periodic trigger and
`Retry(maxAttempts: 5, delay: TimeSpan.FromSeconds(10), backoffFactor: 1, allowRetryAgainAfter: TimeSpan.FromMinutes(45))`:
the run at t = 0 fails, four retries fail at 10, 20, 30 and 40 s and the cycle is exhausted. The
run at t = 30 min fails too: no retry, 45 minutes have not elapsed. The run at t = 60 min fails:
a new cycle opens and retries resume. Had the run at t = 30 min succeeded, the cycle would have
been closed right away.

Retries are logged as warnings, an exhausted cycle as an error, and a recovery as information.
Retry delays are awaited on the `TimeProvider` given to `TriggeredHostedService`
(`TimeProvider.System` by default). Cancellation (host shutdown) interrupts a pending retry and
stops the trigger cleanly.

## Testing

Every time-based trigger takes an optional `TimeProvider` (`TimeProvider.System` by default), and so
does `TriggeredHostedService` for retry delays, so you can drive them deterministically in tests with
`FakeTimeProvider` from the `Microsoft.Extensions.TimeProvider.Testing` package:

```csharp
new PeriodicTrigger(TimeSpan.FromMinutes(5), fakeTimeProvider)
```

Schedules (`DailySchedule`, `CronSchedule`) are pure functions of time and need no clock at all:
`schedule.GetNextOccurrence(after)`.

## Writing your own schedule

`ScheduleTrigger` waits for the occurrences computed by an `ISchedule`:

```csharp
public interface ISchedule
{
    // The next occurrence strictly after `after`, in UTC, or null when there is none.
    DateTimeOffset? GetNextOccurrence(DateTimeOffset after);
}
```

Return the next instant the service should run; return `null` to end the trigger. The trigger waits
until that instant, calls `execute`, and asks again. Time zones, calendars and holidays are the
schedule's business, and it can be tested without a clock. This is also how you plug in a cron library
of your choice — here [Cronos](https://github.com/HangfireIO/Cronos), whose expressions accept seconds
and `L`, `W`, `#`:

```csharp
public sealed class CronosSchedule(string expression, TimeZoneInfo timeZone) : ISchedule
{
    private readonly CronExpression _expression = CronExpression.Parse(expression);

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        return _expression.GetNextOccurrence(after, timeZone);
    }
}

new ScheduleTrigger(new CronosSchedule("0 0 L * *", TimeZoneInfo.Utc))
```

## Writing your own trigger

Implement `IServiceTrigger`:

```csharp
public interface IServiceTrigger
{
    Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken);
}
```

Call `execute` whenever the service should run, and return when `cancellationToken` is cancelled.
`execute` returns as soon as the service has taken the firing into account — iteration started,
queued or dropped according to the `OverlapPolicy` — and never waits for the iteration itself, so a
trigger is never blocked by a slow iteration. Failures are handled by the service according to its
`FailureHandlingMode`; `execute` does not throw for them. It does throw `OperationCanceledException`
when `cancellationToken` is cancelled, which the trigger should let end its loop.

`execute` returns a `Firing`: `IsDropped` tells whether the `OverlapPolicy` dropped it, and
`Completion` completes once its iteration is over (succeeded, failed or abandoned at shutdown; it never
faults). Most triggers ignore it; a trigger that paces itself on the iterations awaits `Completion`, as
`FixedDelayTrigger` does. To test a trigger, return `new Firing(task)` or `Firing.Dropped` from your fake
`execute`.

This is, in full, what `NotificationTrigger` does:

```csharp
public class OnSignalTrigger(SemaphoreSlim signal) : IServiceTrigger
{
    public async Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(cancellationToken);
                await execute(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
```
