using System.Diagnostics;
using NHapi.Base.Parser;
using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Strategies;

namespace HealthBridge.HL7Service.Services;

/// <summary>
/// Core service that parses raw HL7 v2 pipe-delimited messages into structured data.
/// </summary>
public interface IHL7ParserService
{
    HL7ParseResult ParseMessage(string rawMessage);
}

/// <summary>
/// Parses HL7 v2 messages using the nHapi library and delegates data extraction
/// to the appropriate strategy based on message type.
///
/// Flow: raw text → nHapi PipeParser → typed IMessage → strategy → PatientData
///
/// nHapi is the .NET port of the Java HAPI library — the industry standard for
/// HL7 v2 parsing in healthcare integrations. https://github.com/nHapiNET/nHapi
///
/// Sources (HL7 v2.5; normative text is HL7 International Ch. 2 — message control and
/// encoding rules. The links are the free Caristix rendering of the same definitions):
///   Segment/table browser — https://hl7-definition.caristix.com/v2/HL7v2.5
///   MSH (delimiters in MSH-1/MSH-2, control ID in MSH-10) —
///     https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
/// Full standards map: docs/STANDARDS.md
/// </summary>
public class HL7ParserService : IHL7ParserService
{
    // ActivitySource is the .NET equivalent of OpenTelemetry's Tracer —
    // creates custom spans visible in Jaeger/X-Ray alongside auto-instrumented HTTP spans.
    private static readonly ActivitySource Activity = new("HealthBridge.HL7Service");

    private readonly PipeParser _parser;                                    // nHapi's pipe-delimited parser
    private readonly IEnumerable<IMessageExtractorStrategy> _strategies;    // Injected strategies (ADT, ORU, default)
    private readonly ILogger<HL7ParserService> _logger;

    public HL7ParserService(
        IEnumerable<IMessageExtractorStrategy> strategies,
        ILogger<HL7ParserService> logger)
    {
        _parser = new PipeParser();
        _strategies = strategies;
        _logger = logger;
    }

    public HL7ParseResult ParseMessage(string rawMessage)
    {
        // Custom OpenTelemetry span for the parse operation — appears as a child
        // span under the auto-instrumented HTTP request span in Jaeger.
        using var activity = Activity.StartActivity("hl7.parse");

        try
        {
            // Normalize line endings — HL7 v2.5 Ch. 2.5 defines \r (0x0D) as the segment
            // terminator, not \n or \r\n. Senders that use \n are non-conformant; we accept
            // them anyway because HTTP clients and text editors introduce \n routinely.
            var normalized = rawMessage
                .Replace("\r\n", Hl7Constants.SegmentSeparator)
                .Replace("\n", Hl7Constants.SegmentSeparator)
                .Trim();

            var message = _parser.Parse(normalized);
            var result = new HL7ParseResult { Success = true, MessageType = message.GetStructureName() };

            // Tag the span with semantic information for filtering in trace UIs
            activity?.SetTag("hl7.message_type", result.MessageType);
            activity?.SetTag("hl7.message_size_bytes", normalized.Length);

            // Strategy Pattern: Find the first strategy capable of handling this message
            var strategy = _strategies.FirstOrDefault(s => s.CanHandle(message));
            
            if (strategy != null)
            {
                result.Patient = strategy.Extract(message);
            }
            else
            {
                _logger.LogWarning("No suitable extraction strategy found for {MessageType}", result.MessageType);
            }

            result.MessageId = result.Patient?.MessageId ?? Hl7Constants.UnknownMessageId;
            activity?.SetTag("hl7.message_id", result.MessageId);
            activity?.SetTag("hl7.strategy", strategy?.GetType().Name ?? "None");

            _logger.LogInformation("Parsed HL7 {Type} message: {Id} using strategy {Strategy}",
                result.MessageType, result.MessageId, strategy?.GetType().Name ?? "None");

            return result;
        }
        catch (Exception ex)
        {
            // Mark the span as failed so it shows red in trace UIs
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error", true);

            _logger.LogWarning("Failed to parse HL7 message: {Error}", ex.Message);
            return new HL7ParseResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }
}
