using System.Diagnostics;
using NHapi.Base.Parser;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using HealthBridge.HL7Service.Strategies;

namespace HealthBridge.HL7Service.Services;

/// <summary>
/// Service contract for translating HL7 v2 messages into FHIR R4 resources.
/// Returns either a strongly-typed Bundle or a serialized JSON string.
/// </summary>
public interface IFhirTranslationService
{
    /// <summary>Parse a raw HL7 v2 message and translate it to a FHIR Bundle.</summary>
    Bundle Translate(string rawHl7Message);

    /// <summary>Parse and translate, returning the Bundle serialized as FHIR JSON.</summary>
    string TranslateToJson(string rawHl7Message);
}

/// <summary>
/// Orchestrates HL7 v2 → FHIR R4 translation using injected strategies.
///
/// Flow:
///   1. nHapi PipeParser parses raw HL7 text into a typed IMessage
///   2. The first strategy that CanHandle() the message is selected
///   3. The strategy produces a FHIR Bundle
///   4. Optionally serialized to FHIR JSON via the official FhirJsonSerializer
///
/// This class is open for extension (add new strategies) but closed for
/// modification (Open/Closed Principle). It depends only on abstractions
/// (IFhirTranslatorStrategy) — Dependency Inversion Principle.
///
/// Sources:
///   FHIR R4 (4.0.1) specification — https://www.hl7.org/fhir/R4/
///   FHIR JSON representation      — https://hl7.org/fhir/R4/json.html
///   HL7 v2 → FHIR mapping IG      — https://build.fhir.org/ig/HL7/v2-to-fhir/
///   Firely .NET SDK (serializer)  — https://github.com/FirelyTeam/firely-net-sdk
///   nHapi (HL7 v2 parser)         — https://github.com/nHapiNET/nHapi
/// Per-resource and per-code-system links live on the strategies; see docs/STANDARDS.md §3.
/// </summary>
public class FhirTranslationService : IFhirTranslationService
{
    private static readonly ActivitySource Activity = new("HealthBridge.HL7Service");

    private readonly PipeParser _hl7Parser;
    private readonly IEnumerable<IFhirTranslatorStrategy> _strategies;
    private readonly FhirJsonSerializer _fhirSerializer;
    private readonly ILogger<FhirTranslationService> _logger;

    public FhirTranslationService(
        IEnumerable<IFhirTranslatorStrategy> strategies,
        ILogger<FhirTranslationService> logger)
    {
        _hl7Parser = new PipeParser();
        _strategies = strategies;
        _fhirSerializer = new FhirJsonSerializer(new SerializerSettings { Pretty = true });
        _logger = logger;
    }

    public Bundle Translate(string rawHl7Message)
    {
        // OpenTelemetry span — appears as a child of the HTTP request span in trace UIs
        using var activity = Activity.StartActivity("fhir.translate");

        if (string.IsNullOrWhiteSpace(rawHl7Message))
            throw new ArgumentException("HL7 message must not be empty", nameof(rawHl7Message));

        // Normalize line endings — HL7 v2 spec uses \r as segment terminator
        var normalized = rawHl7Message
            .Replace("\r\n", Hl7Constants.SegmentSeparator)
            .Replace("\n", Hl7Constants.SegmentSeparator)
            .Trim();

        var message = _hl7Parser.Parse(normalized);
        activity?.SetTag("hl7.message_type", message.GetStructureName());

        // Strategy lookup — first matching strategy wins
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(message));
        if (strategy == null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "No translator strategy");
            _logger.LogWarning("No FHIR translator strategy registered for message type {Type}",
                message.GetStructureName());
            throw new NotSupportedException(
                $"No FHIR translator available for message type '{message.GetStructureName()}'");
        }

        var bundle = strategy.Translate(message);
        activity?.SetTag("fhir.bundle_entry_count", bundle.Entry.Count);
        activity?.SetTag("fhir.strategy", strategy.GetType().Name);

        _logger.LogInformation("Translated {Type} to FHIR Bundle with {Count} entries using {Strategy}",
            message.GetStructureName(), bundle.Entry.Count, strategy.GetType().Name);

        return bundle;
    }

    public string TranslateToJson(string rawHl7Message)
    {
        var bundle = Translate(rawHl7Message);
        return _fhirSerializer.SerializeToString(bundle);
    }
}
