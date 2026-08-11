namespace HealthBridge.HL7Service.Builders;

/// <summary>
/// Abstraction for building HL7 v2 ACK (acknowledgement) messages.
/// In real hospital integrations, every inbound HL7 message requires an ACK/NACK
/// response so the sending system knows whether the message was accepted.
///
/// Source: HL7 v2.5 Chapter 2 (message control / acknowledgements).
///   ACK structure — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ACK
///   MSA-1 codes   — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70008
/// </summary>
public interface IAckBuilder
{
    /// <summary>
    /// Builds an ACK (AA) or NACK (AE) response for a given HL7 message ID.
    /// </summary>
    /// <param name="messageId">The MSH-10 control ID from the original message</param>
    /// <param name="success">True = ACK (AA), False = NACK (AE)</param>
    /// <param name="errorDetail">Optional error text included in the ERR segment</param>
    string BuildAck(string messageId, bool success, string? errorDetail = null);
}
