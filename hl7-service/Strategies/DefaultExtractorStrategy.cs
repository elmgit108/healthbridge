using NHapi.Base.Model;
using NHapi.Model.V25.Segment;
using HealthBridge.HL7Service.Models;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Fallback strategy that handles any HL7 message type not covered by a specific extractor.
///
/// Every valid HL7 v2 message has an MSH segment, so we can always extract the
/// message control ID and sending application. Patient data may not be available
/// for all message types (e.g., QRY queries, MFN master file notifications).
///
/// This strategy is registered last in DI so it only matches if no other strategy can.
///
/// Source: MSH is mandatory in every HL7 v2 message —
///   https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
///   Message type codes, table 0076 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70076
/// </summary>
public class DefaultExtractorStrategy : IMessageExtractorStrategy
{
    // Always returns true — acts as catch-all (must be registered last in DI)
    public bool CanHandle(IMessage message) => true;

    public PatientData Extract(IMessage message)
    {
        // MSH is present in every HL7 v2 message — safe to extract directly
        var msh = (MSH)message.GetStructure("MSH");
        return new PatientData
        {
            MessageId  = msh.MessageControlID.Value ?? "",            // MSH-10
            SendingApp = msh.SendingApplication.NamespaceID.Value ?? "" // MSH-3
        };
    }
}
