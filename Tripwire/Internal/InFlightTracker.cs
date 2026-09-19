using System.Collections.Concurrent;

namespace Tripwire.Internal;

/// <summary>
/// Tracks the tasks a service has started without awaiting them — concurrent iterations and pending
/// retries — so they can be awaited at shutdown.
/// </summary>
internal sealed class InFlightTracker
{
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    /// <summary>
    /// Tracks a task. It must never fault. The tracking key only completes after its removal, so that
    /// <see cref="WhenEmptyAsync"/> converges.
    /// </summary>
    public void Track(Task work)
    {
        var tracker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight.TryAdd(tracker.Task, 0);
        _ = work.ContinueWith(
            _ =>
            {
                _inFlight.TryRemove(tracker.Task, out byte _);
                tracker.SetResult();
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Completes when nothing is in flight anymore, including tasks tracked while waiting.
    /// </summary>
    public async Task WhenEmptyAsync()
    {
        while (!_inFlight.IsEmpty)
        {
            await Task.WhenAll(_inFlight.Keys);
        }
    }
}
