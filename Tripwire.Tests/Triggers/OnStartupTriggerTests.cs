using Tripwire.Tests.Support;
using Tripwire.Triggers;

namespace Tripwire.Tests.Triggers;

public class OnStartupTriggerTests
{
    [Fact]
    public async Task Executes_exactly_once_then_completes()
    {
        var trigger = new OnStartupTrigger();
        var calls = 0;

        await trigger.StartAsync(_ => { calls++; return TestFiring.Accepted; }, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Passes_the_token_to_execute()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken? captured = null;

        await new OnStartupTrigger().StartAsync(ct => { captured = ct; return TestFiring.Accepted; }, cts.Token);

        Assert.Equal(cts.Token, captured);
    }

    [Fact]
    public async Task Propagates_exceptions_from_execute()
    {
        // The failure policy is applied by the service; the trigger lets through whatever execute throws.
        var trigger = new OnStartupTrigger();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            trigger.StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_null_execute()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => new OnStartupTrigger().StartAsync(null!, CancellationToken.None));
        Assert.Equal("execute", ex.ParamName);
    }
}
