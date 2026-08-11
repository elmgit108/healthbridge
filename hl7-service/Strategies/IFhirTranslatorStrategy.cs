using NHapi.Base.Model;
using Hl7.Fhir.Model;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Strategy Pattern interface for translating HL7 v2 messages into FHIR R4 resources.
///
/// FHIR (Fast Healthcare Interoperability Resources) is the modern HL7 standard
/// — a REST + JSON specification used by Epic, Cerner, and mandated by CMS for
/// US healthcare interoperability.
///
/// Each HL7 v2 message type maps to a different combination of FHIR resources:
///   ADT^A01 → Patient + Encounter
///   ORU^R01 → Patient + Observation(s) + DiagnosticReport
///
/// Adding support for new message types means writing a new strategy class —
/// no changes to existing code (Open/Closed Principle).
/// </summary>
public interface IFhirTranslatorStrategy
{
    /// <summary>True if this strategy can translate the given parsed HL7 message.</summary>
    bool CanHandle(IMessage message);

    /// <summary>
    /// Produces a FHIR Bundle containing all resources extracted from the HL7 message.
    /// Bundle type is "collection" for translation results — could be "transaction" if posting to a FHIR server.
    /// </summary>
    Bundle Translate(IMessage message);
}
