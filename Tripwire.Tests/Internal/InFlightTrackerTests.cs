using Tripwire.Internal;
using Tripwire.Tests.Support;

namespace Tripwire.Tests.Internal;

public class InFlightTrackerTests
{
    [Fact]
    public async Task WhenEmpty_completes_immediately_when_nothing_is_tracked()
    {
        var tracker = new InFlightTracker();

        await tracker.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WhenEmpty_waits_for_tracked_work()
    {
        var tracker = new InFlightTracker();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Track(gate.Task);

        var waiting = tracker.WhenEmptyAsync();
        await Wait.SettleAsync();
        Assert.False(waiting.IsCompleted);

        gate.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WhenEmpty_also_waits_for_work_tracked_while_waiting()
    {
        // A retry that schedules the next retry before completing must keep the tracker busy.
        var tracker = new InFlightTracker();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Track(first.Task);

        var waiting = tracker.WhenEmptyAsync();

        tracker.Track(second.Task);
        first.SetResult();
        await Wait.SettleAsync();
        Assert.False(waiting.IsCompleted);

        second.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Faulted_work_does_not_break_the_tracker()
    {
        var tracker = new InFlightTracker();
        tracker.Track(Task.FromException(new InvalidOperationException("boom")));

        await tracker.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }
}
