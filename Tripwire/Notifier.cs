namespace Tripwire;

/// <summary>
/// Requests runs of the service whose <see cref="Triggers.NotificationTrigger"/> listens to it. Held by
/// the producer side: a service that has just produced data calls <see cref="Notify"/> so that the
/// service enriching it runs next.
/// </summary>
/// <remarks>
/// A notifier serves one trigger: each notification is handed to a single listener. To fan out, give each
/// consumer its own notifier. Notifications are never lost: those sent before the listening service has
/// started are kept and fire as soon as it does, which guarantees that a service registered after its
/// producer still runs after it.
/// </remarks>
public class Notifier
{
    private readonly SemaphoreSlim _notifications = new(0);

    /// <summary>
    /// Requests one run of the listening service. Never blocks: the firing is handled by the service's
    /// <see cref="OverlapPolicy"/> as any other, so a burst of notifications during an iteration queues,
    /// coalesces or is dropped according to the policy.
    /// </summary>
    public void Notify()
    {
        _notifications.Release();
    }

    /// <summary>
    /// Completes when a notification is available, consuming it.
    /// </summary>
    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        return _notifications.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// A <see cref="Notifier"/> tagged with a type, so that several can be registered in dependency injection
/// without keys. The tag names what the notifier means, and picks which side knows about the other:
/// <list type="bullet">
/// <item>
/// <description>
/// For a pipeline, tag it with the <b>event</b> the producer emits: <c>Notifier&lt;BatchImported&gt;</c>.
/// The producer notifies it, the consumer listens to it, and neither names the other: the dependency runs
/// downstream, from the consumer to the producer's contract, never from the producer to its consumers.
/// </description>
/// </item>
/// <item>
/// <description>
/// For an on-demand run requested from outside (an endpoint, an admin command), tag it with the
/// <b>service</b> to run: <c>Notifier&lt;EnrichmentService&gt;</c>. The caller names its target.
/// </description>
/// </item>
/// </list>
/// </summary>
/// <typeparam name="TTag">The event or the listening service, used as a tag only.</typeparam>
public sealed class Notifier<TTag> : Notifier
{
}
