namespace Tripwire;

/// <summary>
/// Computes when a <see cref="Triggers.ScheduleTrigger"/> runs: a pure function from an instant to the
/// next occurrence, independent of any clock, so it can be tested without a <see cref="TimeProvider"/>.
/// </summary>
public interface ISchedule
{
    /// <summary>
    /// Returns the next occurrence strictly after <paramref name="after"/>, in UTC, or
    /// <see langword="null"/> when there is none: the trigger then completes.
    /// </summary>
    /// <param name="after">The instant after which to look, in any offset.</param>
    DateTimeOffset? GetNextOccurrence(DateTimeOffset after);
}
