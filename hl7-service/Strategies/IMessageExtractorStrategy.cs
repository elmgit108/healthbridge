using NHapi.Base.Model;
using HealthBridge.HL7Service.Models;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Strategy Pattern interface for extracting patient data from different HL7 message types.
///
/// Each HL7 message type (ADT, ORU, ORM, etc.) has a different segment structure.
/// Rather than a giant if/else in the parser, each message type gets its own strategy
/// that knows how to navigate that specific structure.
///
/// New message types can be supported by adding a new strategy class — no need
/// to modify existing code (Open/Closed Principle).
/// </summary>
public interface IMessageExtractorStrategy
{
    /// <summary>Returns true if this strategy can handle the given parsed HL7 message.</summary>
    bool CanHandle(IMessage message);

    /// <summary>Extracts patient demographics and context from the HL7 message segments.</summary>
    PatientData Extract(IMessage message);
}
