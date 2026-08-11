using Microsoft.AspNetCore.Mvc;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Builders;

namespace HealthBridge.HL7Service.Controllers;

/// <summary>
/// REST API for parsing HL7 v2 messages and generating acknowledgements.
///
/// In a real hospital integration, HL7 messages arrive via MLLP (TCP) connections.
/// This service exposes them over HTTP for easier testing, gateway routing, and
/// cloud deployment — a common pattern in modern healthcare middleware.
///
/// Sources:
///   HL7 v2.5 segments, tables and trigger events —
///     https://hl7-definition.caristix.com/v2/HL7v2.5
///     (free rendering; the normative spec is the HL7 International v2.5 PDF at
///      https://www.hl7.org/implement/standards/)
///   MLLP framing (0x0B … 0x1C 0x0D) is defined in the HL7 v2 Transport Specification:
///     MLLP, Release 2 — not implemented here, since we accept HL7 over HTTP.
/// Full standards map: docs/STANDARDS.md
/// </summary>
[ApiController]
[Route("api/hl7")]
public class HL7Controller : ControllerBase
{
    private readonly IHL7ParserService _parser;
    private readonly IAckBuilder _ackBuilder;
    private readonly ILogger<HL7Controller> _logger;

    public HL7Controller(IHL7ParserService parser, IAckBuilder ackBuilder, ILogger<HL7Controller> logger)
    {
        _parser = parser;
        _ackBuilder = ackBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Parse a raw HL7 v2 pipe-delimited message sent as text/plain.
    /// This is the primary endpoint — accepts the standard HL7 wire format.
    /// </summary>
    [HttpPost("parse")]
    [Produces(ContentTypes.Json)]
    [Consumes(ContentTypes.PlainText)]
    public IActionResult Parse()
    {
        using var reader = new StreamReader(Request.Body);
        var rawMessage = reader.ReadToEndAsync().GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(rawMessage))
            return BadRequest(new { error = "Request body must contain an HL7 message" });

        var result = _parser.ParseMessage(rawMessage);

        // Generate ACK and include it both in the JSON body and as a Base64 header.
        // The X-HL7-ACK header mimics how MLLP systems return ACKs inline.
        var ack = _ackBuilder.BuildAck(
            result.MessageId ?? Hl7Constants.UnknownMessageId,
            result.Success,
            result.Error
        );
        result.Acknowledgement = ack;
        Response.Headers.Append("X-HL7-ACK", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ack)));

        return result.Success ? Ok(result) : UnprocessableEntity(result);
    }

    /// <summary>
    /// Parse an HL7 message passed inside a JSON wrapper.
    /// Convenience endpoint — easier to call from Postman/Swagger than raw text/plain.
    /// </summary>
    [HttpPost("parse/json")]
    [Produces(ContentTypes.Json)]
    public IActionResult ParseJson([FromBody] HL7JsonRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message field is required" });

        var result = _parser.ParseMessage(request.Message);
        result.Acknowledgement = _ackBuilder.BuildAck(result.MessageId ?? Hl7Constants.UnknownMessageId, result.Success, result.Error);

        return result.Success ? Ok(result) : UnprocessableEntity(result);
    }

    /// <summary>
    /// Generate a standalone ACK or NACK message for a given message control ID.
    /// Useful for testing ACK generation without sending a full HL7 message.
    /// </summary>
    [HttpPost("ack")]
    [Produces(ContentTypes.PlainText)]
    public IActionResult Acknowledge([FromBody] AckRequest request)
    {
        var ack = _ackBuilder.BuildAck(request.MessageId, request.Success, request.ErrorDetail);
        return Content(ack, ContentTypes.PlainText);
    }
}

/// <summary>Request body for the /api/hl7/parse/json endpoint.</summary>
public class HL7JsonRequest
{
    public string Message { get; set; } = "";
}

/// <summary>Request body for the /api/hl7/ack endpoint.</summary>
public class AckRequest
{
    public string MessageId { get; set; } = "";
    public bool Success { get; set; } = true;
    public string? ErrorDetail { get; set; }
}
