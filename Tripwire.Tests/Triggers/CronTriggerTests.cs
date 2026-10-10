using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class CronTriggerTests
{
    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Equal("expression", Assert.Throws<ArgumentNullException>(() => new CronTrigger(null!)).ParamName);
        Assert.Equal("expression", Assert.Throws<ArgumentException>(() => new CronTrigger("not cron")).ParamName);
    }

    [Fact]
    public async Task Fires_at_the_expression_times()
    {
        // 1 January 2024 is a Monday; 13:00 in UTC+5 is 08:00 UTC.
        var timeProvider = new FakeTimeProvider(TestDates.Jan1St0800Utc);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new CronTrigger("30 13 * * MON-FRI", timeProvider, TestTimeZones.UtcPlus5).StartAsync(_ => { calls++; return TestFiring.Accepted; }, cts.Token);

        timeProvider.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(0, calls);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => calls == 1);

        // Let the trigger compute and arm the next occurrence before moving the clock past it.
        await Wait.SettleAsync();

        // Tuesday, same time.
        timeProvider.Advance(TimeSpan.FromHours(24));
        await Wait.UntilAsync(() => calls == 2);

        await cts.CancelAsync();
        await run;
    }
}
