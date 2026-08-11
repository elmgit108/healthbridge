using System;

namespace HealthBridge.HL7Service.Builders;

/// <summary>
/// Constructs HL7 v2 ACK/NACK messages in pipe-delimited format.
///
/// HL7 acknowledgement flow:
///   1. Sender transmits a message (ADT, ORU, etc.)
///   2. Receiver parses the message
///   3. Receiver returns an ACK (accept) or NACK (reject)
///   4. Sender checks MSA-1 code: AA = accepted, AE = error, AR = rejected
///
/// This builder produces a minimal but valid ACK with MSH + MSA + optional ERR segments.
///
/// Sources (HL7 v2.5 — normative text is HL7 International Chapter 2; the links are the
/// free Caristix rendering of the same definitions):
///   ACK message structure — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ACK
///   MSH segment           — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
///   MSA segment           — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSA
///   ERR segment           — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/ERR
///   MSA-1 ack codes, table 0008 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70008
///   ERR error codes, table 0357 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70357
///   MSH-11 processing ID, table 0103 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70103
///
/// Known deviations from the standard (see docs/STANDARDS.md §6): MSH-11 and the version
/// are hardcoded, MSH-5/6 do not echo the sender's MSH-3/4, and the ERR segment does not
/// use the v2.5 field layout (ERR-2 location, ERR-3 code).
/// </summary>
public class Hl7AckBuilder : IAckBuilder
{
    public string BuildAck(string messageId, bool success, string? errorDetail = null)
    {
        // AA = Application Accept, AE = Application Error — HL7 table 0008 (original ack mode).
        // Enhanced mode adds CA/CE/CR, which we do not emit.
        //   https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70008
        var ackCode = success ? "AA" : "AE";
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        // ERR segment is only included on failure. 207 = "Application internal error"
        // in HL7 table 0357 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70357
        var error = errorDetail != null ? $"ERR||207^Application error^HL70357|E|{errorDetail}" : "";

        // HL7 v2 uses \r (carriage return) as the segment delimiter — v2.5 Ch. 2.5
        return string.Join(Hl7Constants.SegmentSeparator,
            $"MSH|^~\\&|HealthBridge|CLOUD|SENDER|SYSTEM|{timestamp}||ACK|ACK{timestamp}|P|2.5",
            $"MSA|{ackCode}|{messageId}|{(success ? "Message accepted" : errorDetail ?? "Error")}",
            error
        ).TrimEnd(Hl7Constants.SegmentSeparatorChar);
    }
}
