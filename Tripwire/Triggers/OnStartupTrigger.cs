namespace Tripwire.Triggers;

/// <summary>
/// Runs the service once, when the application starts.
/// </summary>
public class OnStartupTrigger : IServiceTrigger
{
    /// <inheritdoc />
    public Task StartAsync(Func<CancellationToken, Task<Firing>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        return execute(cancellationToken);
    }
}
