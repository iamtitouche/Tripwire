namespace Tripwire.Tests.Support;

/// <summary>
/// Test-driven trigger: <see cref="FireAsync"/> calls <c>execute</c> on demand,
/// and <see cref="IServiceTrigger.StartAsync"/> only returns when the token is cancelled.
/// </summary>
internal sealed class ManualTrigger : IServiceTrigger
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task>? _execute;

    public Task Started => _started.Task;

    public CancellationToken Token { get; private set; }

    public Task StartAsync(Func<CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        _execute = execute;
        Token = cancellationToken;
        _started.TrySetResult();

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => stopped.TrySetResult());
        return stopped.Task;
    }

    public Task FireAsync()
    {
        if (_execute is null)
        {
            throw new InvalidOperationException("The trigger has not been started.");
        }

        return _execute(Token);
    }
}
