# How to use Tripwire — Q&A

Questions as you might ask them, answers with code you can paste. For the full reference, see the
[README](README.md).

- [Getting started](#getting-started)
- [Choosing when it runs](#choosing-when-it-runs)
- [Slow jobs and overlapping runs](#slow-jobs-and-overlapping-runs)
- [Errors and retries](#errors-and-retries)
- [Chaining services and on-demand runs](#chaining-services-and-on-demand-runs)
- [Dependencies and configuration](#dependencies-and-configuration)
- [Shutdown, deployment and limits](#shutdown-deployment-and-limits)
- [Testing](#testing)
- [Extending Tripwire](#extending-tripwire)

---

## Getting started

### How do I run a background job every 5 minutes in my .NET worker?

Derive from `TriggeredHostedService`, put the work in `ExecuteIteration`, and pass a trigger to the base
constructor:

```csharp
using Tripwire;
using Tripwire.Triggers;

public class CleanupService : TriggeredHostedService
{
    public CleanupService(ILogger<CleanupService> logger)
        : base(new PeriodicTrigger(TimeSpan.FromMinutes(5)), logger: logger)
    {
    }

    protected override async Task ExecuteIteration(CancellationToken cancellationToken)
    {
        await CleanupAsync(cancellationToken);
    }
}
```

Register it like any hosted service:

```csharp
builder.Services.AddHostedService<CleanupService>();
```

### Which .NET versions and hosts does it work with?

.NET 8, 9 and 10. The package only depends on `Microsoft.Extensions.Hosting.Abstractions` and
`Microsoft.Extensions.Logging.Abstractions`, so it works in any generic host: console apps, worker
services, ASP.NET Core.

### Do I really need to pass the logger? It's optional in the constructor.

Pass it. Without a logger, the default failure mode (`Ignore`) swallows exceptions **without any trace**,
and skipped, queued or retried runs are not reported either. Take an `ILogger<YourService>` in your
constructor and hand it to the base class, as in every example here.

### Which namespaces do I need?

- `Tripwire`: `TriggeredHostedService`, `OverlapPolicy`, `FailureHandlingMode`, `Notifier`, `ISchedule`, `IServiceTrigger`, `Firing`
- `Tripwire.Triggers`: `OnStartupTrigger`, `PeriodicTrigger`, `FixedDelayTrigger`, `ScheduleTrigger`, `CronTrigger`, `NotificationTrigger`, `CompositeTrigger`
- `Tripwire.Schedules`: `DailySchedule`, `CronSchedule`

---

## Choosing when it runs

### My PeriodicTrigger doesn't run at startup, only after the first interval. Is that a bug?

No, that's by design: the first run happens one interval after startup. To run immediately *and* then
periodically, combine two triggers:

```csharp
new CompositeTrigger(
    new OnStartupTrigger(),
    new PeriodicTrigger(TimeSpan.FromMinutes(5)))
```

### How do I run something only once when the app starts?

Use `OnStartupTrigger`. Once the run (and its retries, if any) has finished, the service completes; the
rest of the application keeps running.

```csharp
public class WarmupService : TriggeredHostedService
{
    public WarmupService(ILogger<WarmupService> logger)
        : base(new OnStartupTrigger(), logger: logger)
    {
    }

    protected override Task ExecuteIteration(CancellationToken cancellationToken) => WarmCachesAsync(cancellationToken);
}
```

### How do I run a job every day at 8:00 and 20:00?

Use `ScheduleTrigger` with the times of day. They are in UTC unless you pass a time zone:

```csharp
new ScheduleTrigger(
    [new TimeOnly(8, 0), new TimeOnly(20, 0)],
    timeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"))
```

### Can I use a cron expression, like "weekdays at 8am Paris time"?

Yes, with `CronTrigger`:

```csharp
new CronTrigger("0 8 * * MON-FRI", timeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"))
```

The expression has five fields (`minute hour day-of-month month day-of-week`) and supports `*`, lists
(`1,15`), ranges (`9-17`, `MON-FRI`), steps (`*/15`, `1-30/5`, `5/10`), month and day names, `0` or `7`
for Sunday, and the macros `@yearly`, `@annually`, `@monthly`, `@weekly`, `@daily`, `@midnight` and
`@hourly`.

### What happens if my cron expression is invalid?

The `CronTrigger` constructor throws an `ArgumentException` naming the field and the bad token, e.g.
`Invalid cron expression '0 25 * * *': 25 is out of range 0-23 in the hour field.` Since the trigger is
built when the hosted service is created, the application fails at startup rather than silently never
running the job.

An expression that is valid but never matches, like `0 0 31 2 *` (31 February), does not throw: the
trigger just completes without ever firing.

### I need a seconds field / "last day of the month" (`L`) / "second Tuesday" (`#`). Does the cron trigger support that?

No, the built-in parser is standard five-field cron only. Plug in a cron library through `ISchedule`
instead, for example [Cronos](https://github.com/HangfireIO/Cronos) (a separate package):

```csharp
public sealed class CronosSchedule : ISchedule
{
    private readonly Cronos.CronExpression _expression;
    private readonly TimeZoneInfo _timeZone;

    public CronosSchedule(string expression, TimeZoneInfo timeZone)
    {
        _expression = Cronos.CronExpression.Parse(expression);
        _timeZone = timeZone;
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after) => _expression.GetNextOccurrence(after, _timeZone);
}

new ScheduleTrigger(new CronosSchedule("0 0 L * *", TimeZoneInfo.Utc))
```

### What happens with daylight saving time? Will my 2:30 AM job run twice or not at all?

Both `ScheduleTrigger` and `CronTrigger` handle it the same way:

- **Spring forward**: a time that does not exist that day (02:30 when clocks jump from 02:00 to 03:00) is
  skipped for that day.
- **Fall back**: a time that occurs twice (02:30 when clocks go from 03:00 back to 02:00) runs **once**,
  at its first instance (daylight time).
- **Cron with an interval in the hour field** (`*/15 * * * *`, `0 * * * *`) runs in **both** instances of
  the repeated hour, so the interval is kept.

If you want to avoid the question entirely, schedule in UTC (the default).

### Can a service have several triggers, e.g. a daily schedule plus manual runs?

Yes, wrap them in a `CompositeTrigger`: the service runs whenever any of them fires.

```csharp
new CompositeTrigger(
    new CronTrigger("0 2 * * *"),
    new NotificationTrigger(notifier))
```

All inner triggers share the service's `OverlapPolicy` and `FailureHandlingMode`, so a manual run that
lands during the nightly run is queued (or dropped, with `Skip`), not run in parallel.

---

## Slow jobs and overlapping runs

### My job sometimes takes longer than the interval. What happens? Will runs pile up?

Most triggers never wait for the job: they keep firing on time, and what happens to a firing that lands while
a run is in progress is decided by the `OverlapPolicy`. By default (`OverlapPolicy.Wait`) runs never
overlap and firings **queue up without limit**, with a warning in the logs each time one has to wait.
If your job is regularly slower than its trigger, that queue will grow: pick another policy, or a
`FixedDelayTrigger`, which waits for each run to finish (see the next question).

### What's the difference between PeriodicTrigger and FixedDelayTrigger? Which one should I use?

Both repeat a job; they differ in what the interval is measured from.

- **`PeriodicTrigger` runs at a fixed rate**: it fires every interval by the clock, whether or not the
  previous run has finished. With a 1-minute interval and 3-minute runs, it still fires every minute, and
  the `OverlapPolicy` decides what happens to the extra firings (queued, skipped or run in parallel).
- **`FixedDelayTrigger` runs with a fixed delay**: it waits for the run to finish, then waits the delay,
  then fires again. With a 1-minute delay and 3-minute runs, a run starts every 4 minutes. Its own
  firings never overlap each other, so on its own it never queues anything.

That last guarantee only covers the trigger's **own** runs. In a `CompositeTrigger`, the other triggers
keep firing on their own clock and their firings are handled by the `OverlapPolicy` as usual: they can
queue behind a `FixedDelayTrigger` run, or run alongside it with `Concurrent`. Its own firing can also
wait behind another trigger's run, which pushes its next start back by that wait.

| | `PeriodicTrigger` | `FixedDelayTrigger` |
|---|---|---|
| Interval measured from | the previous firing | the **end** of its previous run |
| Slow run | firings keep coming, handled by the `OverlapPolicy` | its next run is pushed back |
| Time between two of its starts | the interval | the delay + the duration of its run (+ any wait behind another trigger's run) |
| First run | one interval after startup | after `initialDelay` (the delay by default, `TimeSpan.Zero` for right away) |
| Good for | a steady cadence: metrics, reports, heartbeats | jobs of variable duration: polling, syncing, draining a queue, rate-limited APIs |

```csharp
public class InboxPoller : TriggeredHostedService
{
    // Runs right away, then 30 seconds after the end of each run, however long it takes.
    public InboxPoller(ILogger<InboxPoller> logger)
        : base(new FixedDelayTrigger(TimeSpan.FromSeconds(30), initialDelay: TimeSpan.Zero), logger: logger)
    {
    }

    protected override Task ExecuteIteration(CancellationToken cancellationToken) => PollInboxAsync(cancellationToken);
}
```

A few details about `FixedDelayTrigger`:

- The delay also starts after a **failed** run. Retries from `FailureHandlingMode.Retry` are separate
  firings: the delay does not wait for them and they do not reset it.
- In a `CompositeTrigger`, if its firing has to wait behind another trigger's run, the delay starts
  when its own run finishes; if the firing is dropped (`Skip`), the delay simply starts again.
- If you want "right away, then every 30 seconds after each run", use `initialDelay: TimeSpan.Zero`
  rather than combining it with an `OnStartupTrigger`: the startup run would not wait for the delay chain.

### How do I make sure only one run happens at a time and just skip the ticks that overlap?

Use `OverlapPolicy.Skip`:

```csharp
public class SyncService : TriggeredHostedService
{
    public SyncService(ILogger<SyncService> logger)
        : base(
            new PeriodicTrigger(TimeSpan.FromMinutes(1)),
            OverlapPolicy.Skip,
            logger: logger)
    {
    }

    // ...
}
```

A skipped firing is logged at information level.

### I don't want to lose the tick, but I don't want 10 runs queued either. Can I get just one catch-up run?

Yes, bound the queue to one pending firing:

```csharp
OverlapPolicy.Wait.WithMaxPending(1)
```

However many firings land during a run, a single run follows it. Firings beyond the limit are dropped
with a warning.

### Can I run several iterations in parallel?

Yes, with `OverlapPolicy.Concurrent(n)`, optionally with a bounded queue:

```csharp
// at most 4 runs at once, at most 10 waiting, anything beyond is dropped
OverlapPolicy.Concurrent(4).WithMaxPending(10)
```

Your `ExecuteIteration` must then be safe to run concurrently.

---

## Errors and retries

### What happens if my job throws an exception?

By default (`FailureHandlingMode.Ignore`), the exception is logged as an error, the run is abandoned, and
the service fires again on the next occurrence. The exception never reaches the host.

An `OperationCanceledException` thrown because the application is shutting down is not treated as a
failure.

### How do I retry a failed run with exponential backoff?

Use `FailureHandlingMode.Retry`:

```csharp
public class ReportUploader : TriggeredHostedService
{
    public ReportUploader(ILogger<ReportUploader> logger)
        : base(
            new CronTrigger("0 6 * * *"),
            failureHandling: FailureHandlingMode.Retry(
                maxAttempts: 5,
                delay: TimeSpan.FromSeconds(10),
                backoffFactor: 2,
                maxDelay: TimeSpan.FromMinutes(5)),
            logger: logger)
    {
    }

    // ...
}
```

Defaults are 3 attempts (the first run included), 1 s initial delay, factor 2, capped at 5 minutes. Use
`backoffFactor: 1` for a constant delay.

### How do retries interact with the regular schedule? Could I end up with a retry and a scheduled run at the same time?

Retries form one **retry cycle per service**, not per run:

- Any failure (a retry or a regular run) counts as an attempt and restarts the wait with the next delay.
- Any success, whichever trigger started it, closes the cycle and cancels the pending retry.
- A retry is dispatched like any firing, so it obeys the `OverlapPolicy`: with the default `Wait` it
  queues behind a running iteration, with `Skip` it can be dropped, with `Concurrent` it can run
  alongside a regular run.
- Once `maxAttempts` is reached, further failures are only logged until a success, or until
  `allowRetryAgainAfter` has elapsed since the first failure of the cycle.

### My job is critical: if it fails, I want the whole app to stop so Kubernetes/systemd restarts it. How?

Use `FailureHandlingMode.StopOnFailure`:

```csharp
failureHandling: FailureHandlingMode.StopOnFailure
```

The exception is logged, `Environment.ExitCode` is set to 1, the service's other runs are cancelled and
the host shuts down gracefully, so the process exits with a failure code. For a specific code, use
`FailureHandlingMode.StopOnFailureWithExitCode(3)`.

This relies on the host's default `BackgroundServiceExceptionBehavior.StopHost`. If you set it to
`Ignore`, the exception is logged and the exit code set, but the application keeps running without the
service.

---

## Chaining services and on-demand runs

### How do I make service B run right after service A finishes its work?

Share a `Notifier`: A calls `Notify()` when it's done, B listens with a `NotificationTrigger`. Tag the
notifier with the **event**, so neither service references the other:

```csharp
public sealed class BatchImported;

public class ImportService : TriggeredHostedService
{
    private readonly Notifier<BatchImported> _batchImported;

    public ImportService(Notifier<BatchImported> batchImported, ILogger<ImportService> logger)
        : base(new PeriodicTrigger(TimeSpan.FromMinutes(10)), logger: logger)
    {
        _batchImported = batchImported;
    }

    protected override async Task ExecuteIteration(CancellationToken cancellationToken)
    {
        await ImportAsync(cancellationToken);
        _batchImported.Notify();
    }
}

public class EnrichmentService : TriggeredHostedService
{
    public EnrichmentService(Notifier<BatchImported> batchImported, ILogger<EnrichmentService> logger)
        : base(new NotificationTrigger(batchImported), logger: logger)
    {
    }

    protected override Task ExecuteIteration(CancellationToken cancellationToken) => EnrichAsync(cancellationToken);
}

builder.Services.AddSingleton<Notifier<BatchImported>>();
builder.Services.AddHostedService<ImportService>();
builder.Services.AddHostedService<EnrichmentService>();
```

The notifier **must be a singleton**, otherwise each service gets its own instance and nothing is ever
received.

### Does the registration order matter? What if A notifies before B has started?

It doesn't matter. Notifications sent before the listening service has started are kept and fire as
soon as it starts.

### Can two services listen to the same notifier?

Not usefully: each notification is handed to a **single** listener, so the two services would split the
notifications between them. To fan out, give each consumer its own notifier (e.g.
`Notifier<EnrichmentService>`, `Notifier<IndexingService>`) and have the producer notify all of them.

### How do I trigger a job manually from my own code, like a controller or a message handler?

Listen with a `NotificationTrigger` on a notifier tagged with the **service**, and call `Notify()` from
wherever you want:

```csharp
builder.Services.AddSingleton<Notifier<EnrichmentService>>();

public class AdminController : ControllerBase
{
    private readonly Notifier<EnrichmentService> _enrich;

    public AdminController(Notifier<EnrichmentService> enrich)
    {
        _enrich = enrich;
    }

    [HttpPost("enrich")]
    public IActionResult Enrich()
    {
        _enrich.Notify();
        return Accepted();
    }
}
```

`Notify()` never blocks and does not wait for the run.

---

## Dependencies and configuration

### How do I use a scoped service like a DbContext in my job? I get "Cannot consume scoped service from singleton".

Hosted services are singletons. Inject `IServiceScopeFactory` and create a scope per run:

```csharp
public class PurgeService : TriggeredHostedService
{
    private readonly IServiceScopeFactory _scopes;

    public PurgeService(IServiceScopeFactory scopes, ILogger<PurgeService> logger)
        : base(new CronTrigger("0 3 * * *"), logger: logger)
    {
        _scopes = scopes;
    }

    protected override async Task ExecuteIteration(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Sessions.Where(s => s.ExpiresAt < DateTime.UtcNow).ExecuteDeleteAsync(cancellationToken);
    }
}
```

### Can I read the cron expression or interval from appsettings.json instead of hardcoding it?

There is no built-in binding, but constructor parameters are available when calling the base
constructor, so bind options as usual and build the trigger from them:

```csharp
public sealed class ReportOptions
{
    public string Cron { get; set; } = "0 6 * * *";
    public string TimeZone { get; set; } = "UTC";
}

public class ReportService : TriggeredHostedService
{
    public ReportService(IOptions<ReportOptions> options, ILogger<ReportService> logger)
        : base(
            new CronTrigger(
                options.Value.Cron,
                timeZone: TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone)),
            logger: logger)
    {
    }

    // ...
}

builder.Services.Configure<ReportOptions>(builder.Configuration.GetSection("Report"));
```

The trigger is built once, at startup: changing the configuration at runtime has no effect until a
restart.

---

## Shutdown, deployment and limits

### What happens to a running job when the application shuts down?

The `CancellationToken` passed to `ExecuteIteration` is signalled, and running iterations are awaited
(within the host's shutdown timeout). Firings still waiting in the queue and pending retries are
abandoned. Pass the token to everything you await so the job stops quickly.

### I deploy 3 replicas on Kubernetes. Will my cron job run 3 times?

Yes. Tripwire schedules in-process and has no coordination between instances. If a job must run once
per cluster, guard `ExecuteIteration` with a distributed lock (database, Redis…), or run the scheduled
services in a single-replica deployment.

### If the app is down at 3:00, does the 3:00 job run when it comes back up?

No. There is no persistence: occurrences missed while the application was stopped are not caught up.
If you need that, record the last successful run yourself and combine an `OnStartupTrigger` with a check
of that record.

### Can I see when a job last ran or will run next?

Not through an API: there are no metrics or health checks built in. Runs, skips, queues, retries and
failures are reported through the logger you pass to the service.

---

## Testing

### How do I unit test that my job runs at the right times without waiting for real time?

Schedules are pure functions of time, so test them without any clock:

```csharp
var schedule = new CronSchedule("0 8 * * MON-FRI", TimeZoneInfo.Utc);

var next = schedule.GetNextOccurrence(new DateTimeOffset(2024, 1, 6, 12, 0, 0, TimeSpan.Zero)); // a Saturday

Assert.Equal(new DateTimeOffset(2024, 1, 8, 8, 0, 0, TimeSpan.Zero), next); // Monday 08:00
```

### How do I test a trigger with a fake clock?

Every time-based trigger takes an optional `TimeProvider`. Use `FakeTimeProvider` from the
`Microsoft.Extensions.TimeProvider.Testing` package:

```csharp
var time = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
var trigger = new PeriodicTrigger(TimeSpan.FromMinutes(5), time);
var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var cts = new CancellationTokenSource();

var running = trigger.StartAsync(
    _ => { fired.TrySetResult(); return Task.FromResult(new Firing(Task.CompletedTask)); },
    cts.Token);

time.Advance(TimeSpan.FromMinutes(5));
await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

cts.Cancel();
await running;
```

Await a signal rather than asserting right after `Advance`: the callback runs on a continuation. The
callback returns a `Firing`, the service's answer to the trigger: `new Firing(task)` for a run that is
over when `task` completes, `Firing.Dropped` to simulate a firing dropped by the overlap policy.

### My retry delays use real time in tests. How do I control them?

Pass the same `FakeTimeProvider` to the service: `TriggeredHostedService` takes a `timeProvider`
parameter used for retry delays. Triggers and service each take their own, so pass it to both.

When testing a whole service through `StartAsync`, note that `StartAsync` can return before the trigger
has created its timer: wait until the trigger is running before advancing the fake clock.

---

## Extending Tripwire

### How do I write my own schedule, e.g. business days excluding public holidays?

Implement `ISchedule`: return the next occurrence strictly after `after`, or `null` to stop.

```csharp
public sealed class BusinessDaysSchedule : ISchedule
{
    private readonly DailySchedule _daily;
    private readonly TimeZoneInfo _timeZone;
    private readonly IReadOnlySet<DateOnly> _holidays;

    public BusinessDaysSchedule(TimeOnly time, TimeZoneInfo timeZone, IReadOnlySet<DateOnly> holidays)
    {
        _daily = new DailySchedule([time], timeZone);
        _timeZone = timeZone;
        _holidays = holidays;
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var next = _daily.GetNextOccurrence(after)!.Value;

        while (!IsBusinessDay(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, _timeZone).DateTime)))
        {
            next = _daily.GetNextOccurrence(next)!.Value;
        }

        return next;
    }

    private bool IsBusinessDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(day);
}

new ScheduleTrigger(new BusinessDaysSchedule(new TimeOnly(9, 0), parisTimeZone, holidays))
```

An exception thrown by a schedule stops the trigger and, through it, the application, whatever the
`FailureHandlingMode`: keep `GetNextOccurrence` free of I/O that can fail.

### How do I write my own trigger, e.g. one fired by a message queue or a file watcher?

Implement `IServiceTrigger`. Call `execute` each time the service should run, and return when the token
is cancelled. `execute` returns as soon as the firing is handled (started, queued or dropped) and never
throws for job failures; it throws `OperationCanceledException` on shutdown, which should end your loop.
It returns a `Firing`, which most triggers ignore; await `firing.Completion` if your trigger must wait
for the run to finish, as `FixedDelayTrigger` does, and check `firing.IsDropped` to know whether it ran.

```csharp
public class FileDropTrigger : IServiceTrigger
{
    private readonly string _folder;

    public FileDropTrigger(string folder)
    {
        _folder = folder;
    }

    public async Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        var signal = new SemaphoreSlim(0);
        using var watcher = new FileSystemWatcher(_folder) { EnableRaisingEvents = true };
        watcher.Created += (_, _) => signal.Release();

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

Always pass the `cancellationToken` you received to `execute`: it is the token your job receives, so
the job is only cancelled at shutdown if you do.
