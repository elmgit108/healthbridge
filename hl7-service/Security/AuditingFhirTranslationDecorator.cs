using Hl7.Fhir.Model;
using HealthBridge.HL7Service.Services;
using Microsoft.AspNetCore.Http;

namespace HealthBridge.HL7Service.Security;

/// <summary>
/// Decorator that adds HIPAA audit logging to FHIR translation operations.
///
/// Same pattern as AuditingHL7ParserDecorator — wraps the real translation
/// service and emits an audit event after each translation. The wrapped
/// service is unchanged (Open/Closed Principle).
///
/// Audit events from this decorator have Action="FHIR_TRANSLATE" and
/// ResourceType="Bundle" for filtering in SIEM tools.
/// </summary>
public class AuditingFhirTranslationDecorator : IFhirTranslationService
{
    private readonly IFhirTranslationService _inner;
    private readonly IPhiAuditService _audit;
    private readonly IHttpContextAccessor _httpContext;

    public AuditingFhirTranslationDecorator(
        IFhirTranslationService inner,
        IPhiAuditService audit,
        IHttpContextAccessor httpContext)
    {
        _inner = inner;
        _audit = audit;
        _httpContext = httpContext;
    }

    public Bundle Translate(string rawHl7Message)
    {
        Bundle? result = null;
        Exception? error = null;
        try
        {
            result = _inner.Translate(rawHl7Message);
            return result;
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            // Pull patient ID from the resulting bundle if available
            var patientId = ExtractPatientId(result);

            var ctx = _httpContext.HttpContext;
            var auditEvent = new PhiAuditEvent(
                EventId:      Guid.NewGuid().ToString(),
                Timestamp:    DateTime.UtcNow,
                Action:       "FHIR_TRANSLATE",
                ResourceType: "Bundle",
                PatientId:    patientId,
                UserId:       ctx?.User?.Identity?.Name ?? "anonymous",
                SourceIp:     ctx?.Connection?.RemoteIpAddress?.ToString() ?? "unknown",
                RequestId:    ctx?.TraceIdentifier ?? Guid.NewGuid().ToString(),
                Success:      error == null,
                Details:      error?.Message
            );

            _ = _audit.LogAccessAsync(auditEvent);
        }
    }

    public string TranslateToJson(string rawHl7Message)
    {
        // The Translate() call above is what triggers auditing — JSON serialization
        // is just formatting, no separate PHI access event needed.
        return _inner.TranslateToJson(rawHl7Message);
    }

    /// <summary>Extract the patient identifier from the first Patient resource in a Bundle.</summary>
    private static string ExtractPatientId(Bundle? bundle)
    {
        if (bundle == null) return AuditConstants.UnknownValue;

        var patient = bundle.Entry
            .Select(e => e.Resource)
            .OfType<Patient>()
            .FirstOrDefault();

        return patient?.Identifier?.FirstOrDefault()?.Value ?? AuditConstants.UnknownValue;
    }
}
