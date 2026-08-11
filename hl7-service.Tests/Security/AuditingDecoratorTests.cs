using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Security;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Hl7.Fhir.Model;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Security;

/// <summary>
/// Tests for the audit decorators that wrap the parser and translator.
///
/// Two properties matter: every PHI access produces an audit record (HIPAA
/// §164.312(b)), and the decorator never changes what the wrapped service returns.
/// </summary>
public class AuditingDecoratorTests
{
    private static HL7ParserService RealParser() => new(
        new IMessageExtractorStrategy[]
        {
            new AdtExtractorStrategy(),
            new OruExtractorStrategy(),
            new DefaultExtractorStrategy(),
        },
        TestHelpers.NullLoggerFor<HL7ParserService>());

    private static FhirTranslationService RealTranslator() => new(
        new IFhirTranslatorStrategy[] { new AdtToFhirStrategy(), new OruToFhirStrategy() },
        TestHelpers.NullLoggerFor<FhirTranslationService>());

    // --- AuditingHL7ParserDecorator -------------------------------------------

    [Fact]
    public void Parsing_emits_one_audit_event()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        decorator.ParseMessage(Hl7Samples.AdtA01());

        Assert.Single(audit.WaitForEvents(1));
    }

    [Fact]
    public void Parse_audit_event_captures_the_action_patient_and_message_type()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        decorator.ParseMessage(Hl7Samples.AdtA01());

        var recorded = Assert.Single(audit.WaitForEvents(1));
        Assert.Equal("HL7_PARSE", recorded.Action);
        Assert.Equal("ADT_A01", recorded.ResourceType);
        Assert.Equal("PAT001", recorded.PatientId);
        Assert.True(recorded.Success);
    }

    [Fact]
    public void Parse_audit_event_captures_the_caller_identity_and_request_context()
    {
        var audit = new RecordingPhiAuditService();
        var context = TestHelpers.HttpContextWith(userName: "dr.house", ip: "198.51.100.9", traceId: "trace-xyz");
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, context);

        decorator.ParseMessage(Hl7Samples.AdtA01());

        var recorded = Assert.Single(audit.WaitForEvents(1));
        Assert.Equal("dr.house", recorded.UserId);
        Assert.Equal("198.51.100.9", recorded.SourceIp);
        Assert.Equal("trace-xyz", recorded.RequestId);
    }

    [Fact]
    public void Unauthenticated_requests_are_audited_as_anonymous()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        decorator.ParseMessage(Hl7Samples.AdtA01());

        Assert.Equal("anonymous", Assert.Single(audit.WaitForEvents(1)).UserId);
    }

    [Fact]
    public void Auditing_works_with_no_HTTP_context_at_all()
    {
        // Background jobs and tests call the service outside a request; the decorator
        // must still produce a record rather than throwing a NullReferenceException.
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.NoHttpContext());

        decorator.ParseMessage(Hl7Samples.AdtA01());

        var recorded = Assert.Single(audit.WaitForEvents(1));
        Assert.Equal("anonymous", recorded.UserId);
        Assert.Equal("unknown", recorded.SourceIp);
        Assert.NotEmpty(recorded.RequestId);
    }

    [Fact]
    public void Failed_parses_are_audited_with_the_error_detail()
    {
        // A failed parse still touched inbound PHI, so it must appear in the audit trail.
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        decorator.ParseMessage(Hl7Samples.Garbage);

        var recorded = Assert.Single(audit.WaitForEvents(1));
        Assert.False(recorded.Success);
        Assert.NotNull(recorded.Details);
    }

    [Fact]
    public void Decorator_returns_the_wrapped_parser_result_unchanged()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        var direct = RealParser().ParseMessage(Hl7Samples.AdtA01());
        var decorated = decorator.ParseMessage(Hl7Samples.AdtA01());

        Assert.Equal(direct.Success, decorated.Success);
        Assert.Equal(direct.MessageId, decorated.MessageId);
        Assert.Equal(direct.MessageType, decorated.MessageType);
        Assert.Equal(direct.Patient!.PatientId, decorated.Patient!.PatientId);
    }

    [Fact]
    public void An_audit_backend_failure_does_not_break_parsing()
    {
        // Audit writes are fire-and-forget. A failing audit store must not take the
        // request down with it — the log records the problem instead.
        var audit = new RecordingPhiAuditService { ThrowOnLog = new IOException("audit store down") };
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        var result = decorator.ParseMessage(Hl7Samples.AdtA01());

        Assert.True(result.Success);
        Assert.Equal("PAT001", result.Patient!.PatientId);
    }

    [Fact]
    public void Each_audit_event_gets_its_own_id()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        decorator.ParseMessage(Hl7Samples.AdtA01());
        decorator.ParseMessage(Hl7Samples.OruR01());

        var events = audit.WaitForEvents(2);
        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public void Audit_timestamps_are_recorded_in_UTC()
    {
        // Mixed local/UTC timestamps make an audit trail unusable for correlation.
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingHL7ParserDecorator(RealParser(), audit, TestHelpers.HttpContextWith());

        decorator.ParseMessage(Hl7Samples.AdtA01());

        Assert.Equal(DateTimeKind.Utc, Assert.Single(audit.WaitForEvents(1)).Timestamp.Kind);
    }

    // --- AuditingFhirTranslationDecorator --------------------------------------

    [Fact]
    public void Translation_emits_a_FHIR_TRANSLATE_audit_event()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingFhirTranslationDecorator(RealTranslator(), audit, TestHelpers.HttpContextWith());

        decorator.Translate(Hl7Samples.AdtA01());

        var recorded = Assert.Single(audit.WaitForEvents(1));
        Assert.Equal("FHIR_TRANSLATE", recorded.Action);
        Assert.Equal("Bundle", recorded.ResourceType);
        Assert.True(recorded.Success);
    }

    [Fact]
    public void Translation_audit_pulls_the_patient_id_out_of_the_resulting_bundle()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingFhirTranslationDecorator(RealTranslator(), audit, TestHelpers.HttpContextWith());

        decorator.Translate(Hl7Samples.OruR01());

        Assert.Equal("PAT002", Assert.Single(audit.WaitForEvents(1)).PatientId);
    }

    [Fact]
    public void A_failed_translation_is_still_audited_and_the_exception_still_propagates()
    {
        // The audit runs in a finally block, so a caller seeing an exception must
        // still find the attempt recorded.
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingFhirTranslationDecorator(RealTranslator(), audit, TestHelpers.HttpContextWith());

        Assert.Throws<NotSupportedException>(() => decorator.Translate(Hl7Samples.SiuS12()));

        var recorded = Assert.Single(audit.WaitForEvents(1));
        Assert.False(recorded.Success);
        Assert.Equal("Unknown", recorded.PatientId);
        Assert.Contains("SIU_S12", recorded.Details);
    }

    [Fact]
    public void Decorator_returns_the_wrapped_bundle_unchanged()
    {
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingFhirTranslationDecorator(RealTranslator(), audit, TestHelpers.HttpContextWith());

        var bundle = decorator.Translate(Hl7Samples.AdtA01());

        Assert.Equal(Bundle.BundleType.Collection, bundle.Type);
        Assert.Equal(2, bundle.Entry.Count);
    }

    [Fact]
    public void KNOWN_DEFECT_TranslateToJson_emits_no_audit_event_at_all()
    {
        // This test documents a live defect rather than desired behaviour.
        //
        // AuditingFhirTranslationDecorator.TranslateToJson calls _inner.TranslateToJson(),
        // and the inner FhirTranslationService.TranslateToJson calls its OWN Translate() —
        // never the decorator's. So the audit block in the decorator's Translate() never
        // runs on this path, and NO audit event is produced.
        //
        // That matters because /api/fhir/translate and /api/fhir/translate/json — the only
        // two FHIR endpoints — both call TranslateToJson. Every FHIR translation of real
        // PHI therefore goes unaudited, which is the control HIPAA §164.312(b) requires.
        //
        // Fix: have the decorator serialize the bundle it already audits, e.g.
        //   public string TranslateToJson(string raw) =>
        //       new FhirJsonSerializer(...).SerializeToString(Translate(raw));
        // then replace this test with one asserting a single FHIR_TRANSLATE event.
        var audit = new RecordingPhiAuditService();
        var decorator = new AuditingFhirTranslationDecorator(
            RealTranslator(), audit, TestHelpers.HttpContextWith());

        var json = decorator.TranslateToJson(Hl7Samples.AdtA01());

        Assert.Contains("PAT001", json);          // the PHI was translated and returned...
        Assert.Empty(audit.WaitForEvents(1, timeoutMs: 200));   // ...with nothing recorded
    }
}
