using Moq;
using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class ScheduleTriggerTests
{
    /// <summary>
    /// Schedule defined by a lambda, to drive the trigger's loop without a real schedule.
    /// </summary>
    private static ISchedule MockSchedule(Func<DateTimeOffset, DateTimeOffset?> next)
    {
        var schedule = new Mock<ISchedule>();
        schedule
            .Setup(s => s.GetNextOccurrence(It.IsAny<DateTimeOffset>()))
            .Returns(next);

        return schedule.Object;
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Equal("schedule", Assert.Throws<ArgumentNullException>(() => new ScheduleTrigger((ISchedule)null!)).ParamName);

        // The daily shorthand validates like a DailySchedule.
        Assert.Equal("times", Assert.Throws<ArgumentNullException>(() => new ScheduleTrigger((IEnumerable<TimeOnly>)null!)).ParamName);
        Assert.Equal("times", Assert.Throws<ArgumentException>(() => new ScheduleTrigger([])).ParamName);
    }

    [Fact]
    public async Task Rejects_null_execute()
    {
        var trigger = new ScheduleTrigger([new TimeOnly(9, 0)], new FakeTimeProvider());

        // Bounded: without the check the trigger would silently wait for the scheduled time.
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => trigger.StartAsync(null!, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("execute", ex.ParamName);
    }

    [Fact]
    public async Task Fires_at_the_scheduled_time()
    {
        var timeProvider = new FakeTimeProvider(TestDates.Jan1St0800Utc);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new ScheduleTrigger([new TimeOnly(9, 0)], timeProvider).StartAsync(_ => { calls++; return TestFiring.Accepted; }, cts.Token);

        timeProvider.Advance(TimeSpan.FromMinutes(59));
        await Wait.SettleAsync();
        Assert.Equal(0, calls);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => calls == 1);

        // Let the trigger compute and arm the next occurrence before moving the clock past it.
        await Wait.SettleAsync();

        // Next day, same time.
        timeProvider.Advance(TimeSpan.FromHours(24));
        await Wait.UntilAsync(() => calls == 2);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Waits_in_chunks_for_a_far_occurrence()
    {
        // 60 days ahead: beyond what a single Task.Delay accepts.
        var timeProvider = new FakeTimeProvider(TestDates.Jan1St0800Utc);
        var occurrence = TestDates.Jan1St0800Utc.AddDays(60);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new ScheduleTrigger(MockSchedule(after => after < occurrence ? occurrence : null), timeProvider)
            .StartAsync(_ => { calls++; return TestFiring.Accepted; }, cts.Token);

        for (var day = 0; day < 59; day++)
        {
            timeProvider.Advance(TimeSpan.FromDays(1));
            await Wait.SettleAsync(5);
        }

        Assert.Equal(0, calls);

        timeProvider.Advance(TimeSpan.FromDays(1));
        await Wait.UntilAsync(() => calls == 1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Completes_when_the_schedule_has_no_next_occurrence()
    {
        var timeProvider = new FakeTimeProvider(TestDates.Jan1St0800Utc);
        var calls = 0;

        // One occurrence in an hour, then none.
        var run = new ScheduleTrigger(MockSchedule(after => after < TestDates.Jan1St0800Utc.AddHours(1) ? TestDates.Jan1St0800Utc.AddHours(1) : null), timeProvider)
            .StartAsync(_ => { calls++; return TestFiring.Accepted; }, CancellationToken.None);

        Assert.False(run.IsCompleted);

        timeProvider.Advance(TimeSpan.FromHours(1));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, calls);
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Completes_cleanly_on_cancellation()
    {
        var timeProvider = new FakeTimeProvider(TestDates.Jan1St0800Utc);
        using var cts = new CancellationTokenSource();
        var run = new ScheduleTrigger([new TimeOnly(9, 0)], timeProvider).StartAsync(_ => TestFiring.Accepted, cts.Token);

        await cts.CancelAsync();

        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Propagates_exceptions_from_execute()
    {
        var timeProvider = new FakeTimeProvider(TestDates.Jan1St0800Utc);
        var run = new ScheduleTrigger([new TimeOnly(9, 0)], timeProvider).StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }
}
