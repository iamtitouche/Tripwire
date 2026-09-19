namespace Tripwire;

/// <summary>
/// Behaviour of a <see cref="TriggeredHostedService"/> when the trigger fires
/// while iterations are still running.
/// </summary>
/// <remarks>
/// A policy is defined by two numbers: <see cref="MaxConcurrency"/>, the number of iterations that may run
/// at the same time, and <see cref="MaxPending"/>, the number of firings that may wait for a free slot.
/// A firing that lands when all slots are busy is queued; when the queue is full, it is deliberately ignored.
/// Queued firings run in order, as slots free up.
/// </remarks>
public sealed class OverlapPolicy
{
    private OverlapPolicy(int maxConcurrency, int? maxPending)
    {
        MaxConcurrency = maxConcurrency;
        MaxPending = maxPending;
    }

    /// <summary>
    /// One iteration at a time; firings that land during an iteration wait their turn, without limit.
    /// No firing is lost, and a warning is logged each time one has to wait, since an unbounded queue
    /// means the service cannot keep up with its trigger.
    /// </summary>
    public static OverlapPolicy Wait { get; } = new(1, null);

    /// <summary>
    /// One iteration at a time; firings that land during an iteration are ignored, with an information entry.
    /// Equivalent to <c>Wait.WithMaxPending(0)</c>.
    /// </summary>
    public static OverlapPolicy Skip { get; } = new(1, 0);

    /// <summary>
    /// Runs up to <paramref name="maxConcurrency"/> iterations in parallel. When all slots are busy,
    /// firings wait their turn, without limit. In-flight iterations are awaited when the service stops.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of simultaneous iterations. Must be at least 1.</param>
    public static OverlapPolicy Concurrent(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        return new OverlapPolicy(maxConcurrency, null);
    }

    /// <summary>
    /// Same policy, with a bound on the number of firings waiting for a slot. A firing that lands when
    /// <paramref name="maxPending"/> are already waiting is ignored, with a warning. With 0, nothing ever
    /// waits (<see cref="Skip"/> semantics); with 1, at most one run is pending whatever the number of
    /// firings, which coalesces bursts into a single catch-up run.
    /// </summary>
    /// <param name="maxPending">Maximum number of firings waiting for a slot. Must be at least 0.</param>
    public OverlapPolicy WithMaxPending(int maxPending)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPending);
        return new OverlapPolicy(MaxConcurrency, maxPending);
    }

    internal int MaxConcurrency { get; }

    /// <summary>
    /// Maximum number of firings waiting for a slot, or <see langword="null"/> when unbounded.
    /// </summary>
    internal int? MaxPending { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var name = MaxConcurrency == 1 ? "Wait" : $"Concurrent({MaxConcurrency})";

        return MaxPending switch
        {
            null => name,
            0 when MaxConcurrency == 1 => "Skip",
            _ => $"{name}.WithMaxPending({MaxPending})",
        };
    }
}
