using HealthBridge.HL7Service.Security;

namespace HealthBridge.HL7Service.Tests.TestDoubles;

/// <summary>
/// In-memory IPhiAuditService that records every event instead of writing a file.
///
/// The audit decorators fire their writes without awaiting them (see
/// AuditingHL7ParserDecorator), so tests must wait for the event to land rather than
/// assert immediately — use WaitForEvents().
/// </summary>
public class RecordingPhiAuditService : IPhiAuditService
{
    private readonly List<PhiAuditEvent> _events = new();
    private readonly object _lock = new();

    /// <summary>Set to have LogAccessAsync throw, to prove audit failures don't break callers.</summary>
    public Exception? ThrowOnLog { get; set; }

    public IReadOnlyList<PhiAuditEvent> Events
    {
        get { lock (_lock) { return _events.ToList(); } }
    }

    public Task LogAccessAsync(PhiAuditEvent auditEvent)
    {
        if (ThrowOnLog != null)
            return Task.FromException(ThrowOnLog);

        lock (_lock)
        {
            _events.Add(auditEvent);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Polls until <paramref name="count"/> events have been recorded, or the timeout expires.
    /// Returns the events recorded so far either way, so the assertion reports the real count.
    /// </summary>
    public IReadOnlyList<PhiAuditEvent> WaitForEvents(int count, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Events.Count >= count) break;
            Thread.Sleep(10);
        }
        return Events;
    }
}
