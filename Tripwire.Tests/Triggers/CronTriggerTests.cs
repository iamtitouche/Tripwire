using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class CronTriggerTests
{
    private static readonly DateTimeOffset Jan1_0800Utc = new(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);

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
        var time = new FakeTimeProvider(Jan1_0800Utc);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new CronTrigger("30 13 * * MON-FRI", time, TestTimeZones.UtcPlus5).StartAsync(_ => { calls++; return TestFiring.Accepted; }, cts.Token);

        time.Advance(TimeSpan.FromMinutes(29));
        await Wait.SettleAsync();
        Assert.Equal(0, calls);

        time.Advance(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => calls == 1);

        // Let the trigger compute and arm the next occurrence before moving the clock past it.
        await Wait.SettleAsync();

        // Tuesday, same time.
        time.Advance(TimeSpan.FromHours(24));
        await Wait.UntilAsync(() => calls == 2);

        cts.Cancel();
        await run;
    }
}
