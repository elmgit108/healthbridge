namespace HealthBridge.HL7Service.Security;

/// <summary>
/// HIPAA-compliant audit logging contract for PHI access events.
///
/// HIPAA Security Rule § 164.312(b) requires hardware, software, and procedural
/// mechanisms to record and examine activity in systems containing ePHI.
///
/// Every read, write, parse, or translate operation on patient data must
/// produce an immutable audit record including who, what, when, and where.
///
/// Sources:
///   45 CFR §164.312(b) (audit controls) —
///     https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-C/part-164/subpart-C/section-164.312
///   HIPAA Security Rule overview — https://www.hhs.gov/hipaa/for-professionals/security/index.html
///   NIST SP 800-66r2 (how to implement it) — https://csrc.nist.gov/pubs/sp/800/66/r2/final
/// </summary>
public interface IPhiAuditService
{
    /// <summary>
    /// Records a PHI access event to the audit store.
    /// Implementations should write to an immutable backing store
    /// (S3 with object lock, AWS QLDB, append-only log, etc.).
    /// </summary>
    Task LogAccessAsync(PhiAuditEvent auditEvent);
}

/// <summary>
/// Immutable audit event describing a single PHI access.
///
/// §164.312(b) mandates the control but not the record layout, so this field list is a
/// project decision informed by NIST SP 800-66r2. If a formal audit-record format is ever
/// required, the healthcare-specific one is IHE ATNA / FHIR AuditEvent:
///   https://hl7.org/fhir/R4/auditevent.html
/// </summary>
public record PhiAuditEvent(
    string EventId,        // UUID — unique per audit entry
    DateTime Timestamp,    // UTC timestamp of the access
    string Action,         // e.g. "HL7_PARSE", "FHIR_TRANSLATE", "DICOM_READ"
    string ResourceType,   // e.g. "Patient", "Observation", "DicomStudy"
    string PatientId,      // MRN or other patient identifier accessed
    string UserId,         // Authenticated user (or "anonymous" for POC)
    string SourceIp,       // Client IP address
    string RequestId,      // Correlates with distributed trace ID
    bool Success,          // Whether the access succeeded
    string? Details = null // Optional additional context
);
