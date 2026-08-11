using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Services;
using Microsoft.AspNetCore.Http;

namespace HealthBridge.HL7Service.Security;

/// <summary>
/// Decorator Pattern — wraps the real HL7 parser to add HIPAA audit logging
/// without modifying the parser itself (Open/Closed Principle).
///
/// Every parse call produces an audit event recording:
///   - Who performed the access (from JWT claims, currently "anonymous" for POC)
///   - What patient ID was accessed
///   - When and from where
///   - Whether the operation succeeded
///
/// To enable: change the DI registration in Program.cs from
///   AddSingleton&lt;IHL7ParserService, HL7ParserService&gt;()
/// to using this decorator factory:
///   AddSingleton&lt;HL7ParserService&gt;()
///   AddSingleton&lt;IHL7ParserService&gt;(sp =&gt; new AuditingHL7ParserDecorator(
///       sp.GetRequiredService&lt;HL7ParserService&gt;(), ...));
/// </summary>
public class AuditingHL7ParserDecorator : IHL7ParserService
{
    private readonly IHL7ParserService _inner;
    private readonly IPhiAuditService _audit;
    private readonly IHttpContextAccessor _httpContext;

    public AuditingHL7ParserDecorator(
        IHL7ParserService inner,
        IPhiAuditService audit,
        IHttpContextAccessor httpContext)
    {
        _inner = inner;
        _audit = audit;
        _httpContext = httpContext;
    }

    public HL7ParseResult ParseMessage(string rawMessage)
    {
        // Delegate to the wrapped parser — we don't change parsing logic at all
        var result = _inner.ParseMessage(rawMessage);

        // Capture context for the audit record
        var ctx = _httpContext.HttpContext;
        var auditEvent = new PhiAuditEvent(
            EventId:      Guid.NewGuid().ToString(),
            Timestamp:    DateTime.UtcNow,
            Action:       "HL7_PARSE",
            ResourceType: result.MessageType ?? AuditConstants.UnknownValue,
            PatientId:    result.Patient?.PatientId ?? AuditConstants.UnknownValue,
            UserId:       ctx?.User?.Identity?.Name ?? "anonymous",
            SourceIp:     ctx?.Connection?.RemoteIpAddress?.ToString() ?? "unknown",
            RequestId:    ctx?.TraceIdentifier ?? Guid.NewGuid().ToString(),
            Success:      result.Success,
            Details:      result.Error
        );

        // Fire-and-forget — don't block the response on audit log writes.
        // FilePhiAuditService handles its own errors internally.
        _ = _audit.LogAccessAsync(auditEvent);

        return result;
    }
}
