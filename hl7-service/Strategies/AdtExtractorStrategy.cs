using NHapi.Base.Model;
using NHapi.Model.V25.Message;
using HealthBridge.HL7Service.Models;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Extracts patient data from ADT^A01 (Admit/Visit Notification) messages.
///
/// ADT A01 is one of the most common HL7 messages in hospital systems — it fires
/// whenever a patient is admitted. Key segments:
///   MSH — Message header (sender, timestamp, control ID)
///   PID — Patient identification (name, DOB, MRN, gender, address)
///   PV1 — Patient visit info (ward, room, attending doctor, admission type)
///
/// NHapi parses the raw pipe-delimited text into a strongly-typed ADT_A01 object,
/// and this strategy reads the relevant fields from each segment.
///
/// Sources (HL7 v2.5):
///   ADT^A01 structure — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ADT_A01
///   ADT^A04 / ADT^A08 (same structure in v2.5) —
///     https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ADT_A04
///     https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ADT_A08
///   MSH — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
///   PID — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PID
///   PV1 — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PV1
/// </summary>
public class AdtExtractorStrategy : IMessageExtractorStrategy
{
    // ADT_A01 covers A01 (admit), A04 (register), A08 (update) — nHapi maps them all here
    public bool CanHandle(IMessage message) => message is ADT_A01;

    public PatientData Extract(IMessage message)
    {
        var adt = (ADT_A01)message;
        var msh = adt.MSH;   // Message header segment
        var pid = adt.PID;   // Patient identification segment
        var pv1 = adt.PV1;   // Patient visit segment

        return new PatientData
        {
            MessageId      = msh.MessageControlID.Value ?? "",                       // MSH-10
            SendingApp     = msh.SendingApplication.NamespaceID.Value ?? "",         // MSH-3
            PatientId      = pid.GetPatientIdentifierList(0).IDNumber.Value ?? "",   // PID-3 (MRN) — repeating CX, first only
            FirstName      = pid.GetPatientName(0).GivenName.Value ?? "",            // PID-5.2 (XPN.2)
            LastName       = pid.GetPatientName(0).FamilyName.Surname.Value ?? "",   // PID-5.1 (XPN.1)
            DateOfBirth    = pid.DateTimeOfBirth.Time.Value ?? "",                   // PID-7 (TS: yyyyMMdd[HHmmss])
            Gender         = pid.AdministrativeSex.Value ?? "",                      // PID-8 — table 0001
            Ward           = pv1.AssignedPatientLocation.Room.Value ?? "",           // PV1-3.2 (PL.2 room)
            AdmissionType  = pv1.AdmissionType.Value ?? ""                          // PV1-4 — table 0007 (A/C/E/L/N/R/U)
        };
    }
}
