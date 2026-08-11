using System.Text.Json;

namespace HealthBridge.HL7Service.Security;

/// <summary>
/// Append-only file-based audit log implementation.
///
/// Writes one JSON line per audit event to /var/log/healthbridge/phi-audit.log
/// (configurable via PHI_AUDIT_LOG_PATH). Each line is independently parseable
/// for ingestion into SIEM tools (Splunk, Datadog, ELK, CloudWatch Logs).
///
/// In production, this would be replaced (Liskov-substitutable) with:
///   - S3PhiAuditService — writes to S3 bucket with object lock for tamper-evidence
///   - QldbPhiAuditService — uses AWS QLDB for cryptographically verifiable history
///   - CloudWatchPhiAuditService — direct ingestion into CloudWatch Logs
///
/// The IPhiAuditService abstraction means the calling code never changes.
///
/// Sources:
///   HIPAA Security Rule, audit controls — 45 CFR §164.312(b):
///     https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-C/part-164/subpart-C/section-164.312
///   What an audit control implementation needs — NIST SP 800-66r2:
///     https://csrc.nist.gov/pubs/sp/800/66/r2/final
/// The rule requires mechanisms that record and examine activity in systems containing
/// ePHI; it does not prescribe a format, which is why plain JSON lines are acceptable
/// here. Tamper-evidence (object lock, QLDB) is what the production variants add.
/// See docs/STANDARDS.md §4.
/// </summary>
public class FilePhiAuditService : IPhiAuditService
{
    private readonly string _logPath;
    private readonly ILogger<FilePhiAuditService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FilePhiAuditService(IConfiguration config, ILogger<FilePhiAuditService> logger)
    {
        _logger = logger;
        var configured = config["PHI_AUDIT_LOG_PATH"]
                         ?? Environment.GetEnvironmentVariable("PHI_AUDIT_LOG_PATH")
                         ?? "/var/log/healthbridge/phi-audit.log";

        _logPath = ResolveWritablePath(configured);
    }

    public async Task LogAccessAsync(PhiAuditEvent auditEvent)
    {
        var json = JsonSerializer.Serialize(auditEvent);

        // Serialize writes — multiple concurrent requests would otherwise interleave lines
        await _writeLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logPath, json + Environment.NewLine);
            _logger.LogDebug("PHI audit recorded: {Action} on {ResourceType}/{PatientId}",
                auditEvent.Action, auditEvent.ResourceType, auditEvent.PatientId);
        }
        catch (Exception ex)
        {
            // Audit logging failures are critical — surface them but never block the request
            _logger.LogError(ex, "Failed to write PHI audit log entry");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Tries to use the configured log path; falls back to /tmp if the directory
    /// cannot be created (e.g. running as a non-root container user).
    /// </summary>
    private string ResolveWritablePath(string configured)
    {
        try
        {
            var dir = Path.GetDirectoryName(configured);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return configured;
        }
        catch (Exception ex)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "phi-audit.log");
            _logger.LogWarning(ex,
                "Could not create audit log dir at {Path} — falling back to {Fallback}",
                configured, fallback);
            return fallback;
        }
    }
}
