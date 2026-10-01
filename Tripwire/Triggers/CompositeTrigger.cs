namespace Tripwire.Triggers;

/// <summary>
/// Combines several triggers: the service runs whenever any of them fires.
/// The triggers are started in parallel and share the service's policies.
/// </summary>
public class CompositeTrigger : IServiceTrigger
{
    private readonly IServiceTrigger[] _triggers;

    /// <param name="triggers">Triggers to combine. At least one is required.</param>
    public CompositeTrigger(params IServiceTrigger[] triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        if (triggers.Length == 0)
        {
            throw new ArgumentException("At least one trigger is required.", nameof(triggers));
        }

        if (Array.IndexOf(triggers, null) >= 0)
        {
            throw new ArgumentException("Triggers must not contain null.", nameof(triggers));
        }

        _triggers = triggers;
    }

    /// <inheritdoc />
    /// <remarks>
    /// If one trigger fails, the others are stopped and the exception is propagated,
    /// just as a single trigger would.
    /// </remarks>
    public async Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task>(_triggers.Length);

        try
        {
            foreach (var trigger in _triggers)
            {
                running.Add(trigger.StartAsync(execute, linked.Token));
            }

            while (running.Count > 0)
            {
                var completed = await Task.WhenAny(running);
                running.Remove(completed);

                // Propagate a failure immediately rather than at shutdown, once the others have completed.
                await completed;
            }
        }
        finally
        {
            linked.Cancel();
            await Task.WhenAll(running).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
