namespace Tripwire;

/// <summary>
/// What became of a firing reported by an <see cref="IServiceTrigger"/>: dropped by the service's
/// <see cref="OverlapPolicy"/>, or accepted (started or queued), with a task that completes once it is over.
/// </summary>
/// <remarks>
/// Most triggers ignore it: they fire on their own clock, whatever the iterations do. A trigger that paces
/// itself on the iterations, such as <see cref="Triggers.FixedDelayTrigger"/>, awaits <see cref="Completion"/>.
/// </remarks>
public sealed class Firing
{
    /// <summary>
    /// Creates the outcome of an accepted firing.
    /// </summary>
    /// <param name="completion">Completes once the firing is over. Must never fault.</param>
    public Firing(Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        Completion = completion;
    }

    private Firing()
    {
        IsDropped = true;
        Completion = Task.CompletedTask;
    }

    /// <summary>
    /// A firing ignored by the <see cref="OverlapPolicy"/> because all slots and the queue were full.
    /// Its <see cref="Completion"/> is already completed.
    /// </summary>
    public static Firing Dropped { get; } = new();

    /// <summary>
    /// Whether the firing was ignored by the <see cref="OverlapPolicy"/>, so that no iteration ran for it.
    /// </summary>
    public bool IsDropped { get; }

    /// <summary>
    /// Completes once the firing is over: when its iteration has finished, whether it succeeded or failed,
    /// or when it was abandoned before starting (shutdown, cancelled retry). Never faults. Retries scheduled
    /// by <see cref="FailureHandlingMode.Retry"/> are firings of their own and are not awaited.
    /// </summary>
    public Task Completion { get; }
}
