namespace Tripwire.Tests;

public class FailureHandlingModeTests
{
    [Fact]
    public void Ignore_and_StopOnFailure_are_singletons()
    {
        Assert.Same(FailureHandlingMode.Ignore, FailureHandlingMode.Ignore);
        Assert.Same(FailureHandlingMode.StopOnFailure, FailureHandlingMode.StopOnFailure);
        Assert.NotSame(FailureHandlingMode.Ignore, FailureHandlingMode.StopOnFailure);
    }

    [Fact]
    public void StopOnFailure_defaults_to_exit_code_one()
    {
        Assert.Equal(1, FailureHandlingMode.StopOnFailure.ExitCode);
        Assert.Equal(42, FailureHandlingMode.StopOnFailureWithExitCode(42).ExitCode);
    }

    [Fact]
    public void StopOnFailureWithExitCode_rejects_zero()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FailureHandlingMode.StopOnFailureWithExitCode(0));
        Assert.Equal("exitCode", ex.ParamName);
    }

    [Fact]
    public void Retry_uses_documented_defaults()
    {
        var mode = FailureHandlingMode.Retry();

        Assert.Equal(FailureHandlingMode.Mode.Retry, mode.Kind);
        Assert.Equal(3, mode.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), mode.Delay);
        Assert.Equal(2, mode.BackoffFactor);
        Assert.Equal(TimeSpan.FromMinutes(5), mode.MaxDelay);
        Assert.Equal(TimeSpan.Zero, mode.AllowRetryAgainAfter);
    }

    [Fact]
    public void Retry_keeps_allow_retry_again_after()
    {
        var mode = FailureHandlingMode.Retry(allowRetryAgainAfter: TimeSpan.FromMinutes(45));

        Assert.Equal(TimeSpan.FromMinutes(45), mode.AllowRetryAgainAfter);
    }

    [Fact]
    public void Retry_rejects_negative_allow_retry_again_after()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FailureHandlingMode.Retry(allowRetryAgainAfter: TimeSpan.FromSeconds(-1)));
        Assert.Equal("allowRetryAgainAfter", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Retry_rejects_non_positive_max_attempts(int maxAttempts)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FailureHandlingMode.Retry(maxAttempts));
        Assert.Equal("maxAttempts", ex.ParamName);
    }

    [Fact]
    public void Retry_rejects_backoff_factor_below_one()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FailureHandlingMode.Retry(backoffFactor: 0.5));
        Assert.Equal("backoffFactor", ex.ParamName);
    }

    [Fact]
    public void Retry_rejects_negative_delay()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FailureHandlingMode.Retry(delay: TimeSpan.FromSeconds(-1)));
        Assert.Equal("delay", ex.ParamName);
    }

    [Fact]
    public void Retry_rejects_max_delay_below_delay()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            FailureHandlingMode.Retry(delay: TimeSpan.FromSeconds(10), maxDelay: TimeSpan.FromSeconds(5)));

        Assert.Equal("maxDelay", ex.ParamName);
    }

    [Fact]
    public void Retry_accepts_max_delay_equal_to_delay()
    {
        var mode = FailureHandlingMode.Retry(delay: TimeSpan.FromSeconds(10), maxDelay: TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), mode.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), mode.GetDelay(5));
    }

    [Fact]
    public void GetDelay_grows_geometrically()
    {
        var mode = FailureHandlingMode.Retry(maxAttempts: 10, delay: TimeSpan.FromSeconds(1), backoffFactor: 2);

        Assert.Equal(TimeSpan.FromSeconds(1), mode.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), mode.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), mode.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), mode.GetDelay(4));
    }

    [Fact]
    public void GetDelay_is_constant_with_backoff_factor_one()
    {
        var mode = FailureHandlingMode.Retry(maxAttempts: 10, delay: TimeSpan.FromMilliseconds(500), backoffFactor: 1);

        Assert.All(Enumerable.Range(1, 10), attempt => Assert.Equal(TimeSpan.FromMilliseconds(500), mode.GetDelay(attempt)));
    }

    [Fact]
    public void GetDelay_is_capped_at_max_delay()
    {
        var mode = FailureHandlingMode.Retry(maxAttempts: 10, delay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(8), mode.GetDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(10), mode.GetDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(10), mode.GetDelay(10));
    }

    [Fact]
    public void GetDelay_never_overflows()
    {
        var huge = FailureHandlingMode.Retry(maxAttempts: 1000, delay: TimeSpan.FromSeconds(1), backoffFactor: 1e6);
        var many = FailureHandlingMode.Retry(maxAttempts: 1000);

        Assert.Equal(huge.MaxDelay, huge.GetDelay(1000));
        Assert.Equal(many.MaxDelay, many.GetDelay(1000));
    }

    [Fact]
    public void ToString_describes_the_mode()
    {
        Assert.Equal("Ignore", FailureHandlingMode.Ignore.ToString());
        Assert.Equal("StopOnFailure", FailureHandlingMode.StopOnFailure.ToString());
        Assert.Equal("StopOnFailure(exit code 42)", FailureHandlingMode.StopOnFailureWithExitCode(42).ToString());
        Assert.Equal("Retry(3, 00:00:01, x2, max 00:05:00, again after 00:00:00)", FailureHandlingMode.Retry().ToString());
    }
}

public class OverlapPolicyTests
{
    [Fact]
    public void Wait_and_Skip_have_a_concurrency_of_one()
    {
        Assert.Equal(1, OverlapPolicy.Wait.MaxConcurrency);
        Assert.Equal(1, OverlapPolicy.Skip.MaxConcurrency);
    }

    [Fact]
    public void Concurrent_keeps_the_given_concurrency()
    {
        Assert.Equal(4, OverlapPolicy.Concurrent(4).MaxConcurrency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Concurrent_rejects_non_positive_concurrency(int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlapPolicy.Concurrent(max));
    }

    [Fact]
    public void Wait_and_Concurrent_have_an_unbounded_queue()
    {
        Assert.Null(OverlapPolicy.Wait.MaxPending);
        Assert.Null(OverlapPolicy.Concurrent(4).MaxPending);
    }

    [Fact]
    public void Skip_is_Wait_with_an_empty_queue()
    {
        Assert.Equal(0, OverlapPolicy.Skip.MaxPending);
    }

    [Fact]
    public void WithMaxPending_keeps_the_concurrency_and_bounds_the_queue()
    {
        var policy = OverlapPolicy.Concurrent(4).WithMaxPending(2);

        Assert.Equal(4, policy.MaxConcurrency);
        Assert.Equal(2, policy.MaxPending);
        Assert.Null(OverlapPolicy.Concurrent(4).MaxPending);   // the original is untouched
    }

    [Fact]
    public void WithMaxPending_rejects_a_negative_bound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlapPolicy.Wait.WithMaxPending(-1));
    }

    [Fact]
    public void ToString_describes_the_policy()
    {
        Assert.Equal("Wait", OverlapPolicy.Wait.ToString());
        Assert.Equal("Skip", OverlapPolicy.Skip.ToString());
        Assert.Equal("Skip", OverlapPolicy.Wait.WithMaxPending(0).ToString());
        Assert.Equal("Wait.WithMaxPending(5)", OverlapPolicy.Wait.WithMaxPending(5).ToString());
        Assert.Equal("Concurrent(3)", OverlapPolicy.Concurrent(3).ToString());
        Assert.Equal("Concurrent(3).WithMaxPending(0)", OverlapPolicy.Concurrent(3).WithMaxPending(0).ToString());
        Assert.Equal("Concurrent(3).WithMaxPending(10)", OverlapPolicy.Concurrent(3).WithMaxPending(10).ToString());
    }
}
