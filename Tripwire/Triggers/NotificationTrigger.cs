namespace Tripwire.Triggers;

/// <summary>
/// Runs the service each time its <see cref="Notifier"/> is notified, typically by another service that
/// has just produced the data this one works on. Notifications sent before the service starts are not lost.
/// </summary>
public class NotificationTrigger : IServiceTrigger
{
    private readonly Notifier _notifier;

    /// <param name="notifier">The notifier to listen to. Shared with the notifying side, usually through dependency injection.</param>
    public NotificationTrigger(Notifier notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);

        _notifier = notifier;
    }

    /// <inheritdoc />
    public async Task StartAsync(Func<CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        try
        {
            while (true)
            {
                await _notifier.WaitAsync(cancellationToken);
                await execute(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
