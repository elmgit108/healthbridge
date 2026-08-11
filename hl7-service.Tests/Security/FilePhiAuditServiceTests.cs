using System.Text.Json;
using HealthBridge.HL7Service.Security;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Security;

/// <summary>
/// Tests for the file-backed PHI audit log.
///
/// The control being implemented is HIPAA 45 CFR §164.312(b) (audit controls):
///   https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-C/part-164/subpart-C/section-164.312
///
/// Each test writes into its own temp directory, so runs are isolated and parallel-safe.
/// </summary>
public class FilePhiAuditServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FilePhiAuditServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "healthbridge-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    private string LogPath(string name = "phi-audit.log") => Path.Combine(_tempDir, name);

    private FilePhiAuditService ServiceWriting(string path) => new(
        TestHelpers.ConfigWith(("PHI_AUDIT_LOG_PATH", path)),
        TestHelpers.NullLoggerFor<FilePhiAuditService>());

    private static PhiAuditEvent SampleEvent(string patientId = "PAT001", bool success = true) => new(
        EventId: Guid.NewGuid().ToString(),
        Timestamp: new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        Action: "HL7_PARSE",
        ResourceType: "ADT_A01",
        PatientId: patientId,
        UserId: "dr.house",
        SourceIp: "203.0.113.7",
        RequestId: "trace-abc-123",
        Success: success,
        Details: null);

    [Fact]
    public async Task Writes_an_audit_event_to_the_configured_path()
    {
        var path = LogPath();
        var service = ServiceWriting(path);

        await service.LogAccessAsync(SampleEvent());

        Assert.True(File.Exists(path));
        Assert.Single(await File.ReadAllLinesAsync(path));
    }

    [Fact]
    public async Task Each_event_is_one_self_contained_json_line()
    {
        // SIEM ingestion (Splunk, CloudWatch Logs) parses line-by-line — an event
        // spanning multiple lines would be dropped or split.
        var path = LogPath();
        await ServiceWriting(path).LogAccessAsync(SampleEvent());

        var line = Assert.Single(await File.ReadAllLinesAsync(path));
        using var document = JsonDocument.Parse(line);

        Assert.Equal("HL7_PARSE", document.RootElement.GetProperty("Action").GetString());
        Assert.Equal("PAT001", document.RootElement.GetProperty("PatientId").GetString());
        Assert.Equal("dr.house", document.RootElement.GetProperty("UserId").GetString());
        Assert.Equal("203.0.113.7", document.RootElement.GetProperty("SourceIp").GetString());
        Assert.Equal("trace-abc-123", document.RootElement.GetProperty("RequestId").GetString());
        Assert.True(document.RootElement.GetProperty("Success").GetBoolean());
    }

    [Fact]
    public async Task Records_the_who_what_when_where_the_rule_asks_for()
    {
        var path = LogPath();
        await ServiceWriting(path).LogAccessAsync(SampleEvent());

        var line = Assert.Single(await File.ReadAllLinesAsync(path));
        using var document = JsonDocument.Parse(line);

        foreach (var required in new[] { "EventId", "Timestamp", "Action", "ResourceType", "PatientId", "UserId", "SourceIp", "RequestId", "Success" })
        {
            Assert.True(document.RootElement.TryGetProperty(required, out _), $"missing field: {required}");
        }
    }

    [Fact]
    public async Task Appends_rather_than_overwriting()
    {
        // An audit log that truncates is worse than none — it destroys the evidence
        // it exists to preserve.
        var path = LogPath();
        var service = ServiceWriting(path);

        await service.LogAccessAsync(SampleEvent("PAT001"));
        await service.LogAccessAsync(SampleEvent("PAT002"));
        await service.LogAccessAsync(SampleEvent("PAT003"));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(3, lines.Length);
        Assert.Contains("PAT001", lines[0]);
        Assert.Contains("PAT003", lines[2]);
    }

    [Fact]
    public async Task A_new_instance_appends_to_an_existing_log()
    {
        var path = LogPath();

        await ServiceWriting(path).LogAccessAsync(SampleEvent("PAT001"));
        await ServiceWriting(path).LogAccessAsync(SampleEvent("PAT002"));   // simulates a restart

        Assert.Equal(2, (await File.ReadAllLinesAsync(path)).Length);
    }

    [Fact]
    public async Task Creates_the_log_directory_when_it_does_not_exist()
    {
        var path = Path.Combine(_tempDir, "nested", "deeper", "phi-audit.log");

        await ServiceWriting(path).LogAccessAsync(SampleEvent());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Falls_back_to_a_temp_path_when_the_configured_directory_cannot_be_created()
    {
        // Containers commonly run as a non-root user that cannot write /var/log.
        // The service must degrade instead of crashing the request path.
        var unwritable = Path.Combine("/proc", "healthbridge-cannot-create", "phi-audit.log");
        var fallback = Path.Combine(Path.GetTempPath(), "phi-audit.log");

        // Clear any leftover from a previous run so the assertion proves this call wrote it.
        if (File.Exists(fallback)) File.Delete(fallback);

        var service = ServiceWriting(unwritable);
        await service.LogAccessAsync(SampleEvent());

        Assert.True(File.Exists(fallback), "expected the fallback audit log to be created");
    }

    [Fact]
    public async Task Concurrent_writes_produce_intact_lines()
    {
        // Writes are serialized behind a semaphore. Without it, concurrent appends
        // interleave mid-line and every affected event becomes unparseable.
        var path = LogPath();
        var service = ServiceWriting(path);

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(i => service.LogAccessAsync(SampleEvent($"PAT{i:D4}"))));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(100, lines.Length);

        // Every line must parse independently — that is what proves no interleaving.
        var patientIds = lines
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("PatientId").GetString())
            .ToList();

        Assert.Equal(100, patientIds.Distinct().Count());
    }

    [Fact]
    public async Task Failed_accesses_are_recorded_too()
    {
        // Denied and failed access attempts are precisely what an audit review looks for.
        var path = LogPath();

        await ServiceWriting(path).LogAccessAsync(SampleEvent(success: false));

        var line = Assert.Single(await File.ReadAllLinesAsync(path));
        Assert.False(JsonDocument.Parse(line).RootElement.GetProperty("Success").GetBoolean());
    }

    [Fact]
    public async Task A_write_failure_does_not_propagate_to_the_caller()
    {
        // The decorators call this without awaiting; an escaping exception would
        // surface as an unobserved task exception rather than a handled error.
        var path = LogPath();
        var service = ServiceWriting(path);

        // Replace the log file with a directory so the append cannot succeed.
        File.Delete(path);
        Directory.CreateDirectory(path);

        await service.LogAccessAsync(SampleEvent());   // must not throw
    }
}
