using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class NotificationTriggerTests
{
    [Fact]
    public void Rejects_null_notifier()
    {
        Assert.Equal("notifier", Assert.Throws<ArgumentNullException>(() => new NotificationTrigger(null!)).ParamName);
    }

    [Fact]
    public async Task Rejects_null_execute()
    {
        var trigger = new NotificationTrigger(new Notifier());

        // Bounded: without the check the trigger would silently wait for a notification.
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => trigger.StartAsync(null!, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("execute", ex.ParamName);
    }

    [Fact]
    public async Task Fires_once_per_notification()
    {
        var notifier = new Notifier();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new NotificationTrigger(notifier).StartAsync(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, cts.Token);

        await Wait.SettleAsync();
        Assert.Equal(0, calls);

        notifier.Notify();
        await Wait.UntilAsync(() => calls == 1);

        notifier.Notify();
        notifier.Notify();
        await Wait.UntilAsync(() => calls == 3);

        await Wait.SettleAsync();
        Assert.Equal(3, calls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Notifications_sent_before_start_are_kept()
    {
        var notifier = new Notifier();
        notifier.Notify();
        notifier.Notify();

        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new NotificationTrigger(notifier).StartAsync(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, cts.Token);

        await Wait.UntilAsync(() => calls == 2);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Hands_its_token_to_execute()
    {
        var notifier = new Notifier();
        using var cts = new CancellationTokenSource();
        CancellationToken? received = null;
        var run = new NotificationTrigger(notifier).StartAsync(ct => { received = ct; return Task.CompletedTask; }, cts.Token);

        notifier.Notify();
        await Wait.UntilAsync(() => received is not null);

        Assert.Equal(cts.Token, received);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Completes_cleanly_on_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var run = new NotificationTrigger(new Notifier()).StartAsync(_ => Task.CompletedTask, cts.Token);

        cts.Cancel();

        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Propagates_exceptions_from_execute()
    {
        var notifier = new Notifier();
        var run = new NotificationTrigger(notifier).StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);

        notifier.Notify();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }

    [Fact]
    public void Typed_notifier_is_a_notifier()
    {
        Assert.IsAssignableFrom<Notifier>(new Notifier<NotificationTriggerTests>());
    }
}
