using NHapi.Base.Model;
using NHapi.Model.V25.Message;
using HealthBridge.HL7Service.Models;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Extracts patient data from ORU^R01 (Observation Result) messages.
///
/// ORU R01 is the standard HL7 message for lab results and clinical observations.
/// Structure is nested: PATIENT_RESULT → PATIENT → PID, and separately ORDER → OBR/OBX.
///
/// Key segments:
///   MSH — Message header
///   PID — Patient identification (nested inside PATIENT_RESULT group)
///   OBR — Observation request (what test was ordered)
///   OBX — Observation result (individual result values — WBC, glucose, etc.)
///
/// This strategy extracts the patient demographics; the OBR/OBX lab values
/// could be extracted in a future enhancement.
///
/// Sources (HL7 v2.5 — observation reporting is Chapter 7):
///   ORU^R01 structure — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ORU_R01
///   MSH — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/MSH
///   PID — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PID
///   OBR — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/OBR
///   OBX — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/OBX
/// </summary>
public class OruExtractorStrategy : IMessageExtractorStrategy
{
    public bool CanHandle(IMessage message) => message is ORU_R01;

    public PatientData Extract(IMessage message)
    {
        var oru = (ORU_R01)message;
        var msh = oru.MSH;
        // ORU R01 nests PID inside PATIENT_RESULT(0) → PATIENT group
        var pid = oru.GetPATIENT_RESULT(0).PATIENT.PID;

        return new PatientData
        {
            MessageId   = msh.MessageControlID.Value ?? "",                       // MSH-10
            SendingApp  = msh.SendingApplication.NamespaceID.Value ?? "",         // MSH-3
            PatientId   = pid.GetPatientIdentifierList(0).IDNumber.Value ?? "",   // PID-3 (MRN) — repeating CX, first only
            FirstName   = pid.GetPatientName(0).GivenName.Value ?? "",            // PID-5.2 (XPN.2)
            LastName    = pid.GetPatientName(0).FamilyName.Surname.Value ?? "",   // PID-5.1 (XPN.1)
            DateOfBirth = pid.DateTimeOfBirth.Time.Value ?? "",                   // PID-7 (TS: yyyyMMdd[HHmmss])
            Gender      = pid.AdministrativeSex.Value ?? ""                       // PID-8 — table 0001
        };
    }
}
