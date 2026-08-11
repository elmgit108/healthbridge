using NHapi.Base.Model;
using NHapi.Model.V25.Message;
using NHapi.Model.V25.Segment;
using Hl7.Fhir.Model;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Translates HL7 v2 ORU^R01 (Observation Result) messages into FHIR R4 resources.
///
/// Output Bundle contains:
///   - Patient resource         (from PID)
///   - DiagnosticReport         (from OBR — represents the test order)
///   - Observation resources    (one per OBX — individual result values)
///
/// Field mappings:
///   PID-3,5,7,8  → Patient (same as ADT)
///   OBR-4        → DiagnosticReport.code
///   OBX-3        → Observation.code (LOINC code if present)
///   OBX-5        → Observation.valueQuantity.value
///   OBX-6        → Observation.valueQuantity.unit
///   OBX-7        → Observation.referenceRange
///   OBX-8        → Observation.interpretation (N=normal, H=high, L=low)
///
/// Sources — source format (HL7 v2.5, observation reporting is Chapter 7):
///   ORU^R01 — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ORU_R01
///   OBR — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/OBR
///   OBX — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/OBX
///   OBX-2 value types, table 0125 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70125
///   OBX-8 abnormal flags, table 0078 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70078
///
/// Sources — target format (FHIR R4 4.0.1, normative):
///   Observation      — https://hl7.org/fhir/R4/observation.html
///   DiagnosticReport — https://hl7.org/fhir/R4/diagnosticreport.html
///   Patient / Bundle — https://hl7.org/fhir/R4/patient.html, https://hl7.org/fhir/R4/bundle.html
///
/// Sources — the mapping itself: HL7 "Version 2 to FHIR" IG, https://build.fhir.org/ig/HL7/v2-to-fhir/
///
/// Known gaps (docs/STANDARDS.md §6): only OBX-2 = NM is translated, and codes/units are
/// asserted as LOINC/UCUM without checking the coding-system component that declares them.
/// </summary>
public class OruToFhirStrategy : IFhirTranslatorStrategy
{
    public bool CanHandle(IMessage message) => message is ORU_R01;

    public Bundle Translate(IMessage message)
    {
        var oru = (ORU_R01)message;

        // ORU R01 nests PID inside PATIENT_RESULT(0).PATIENT
        var patientResult = oru.GetPATIENT_RESULT(0);
        var patient = BuildPatient(patientResult.PATIENT.PID);

        // Build observations from each OBX in each ORDER_OBSERVATION
        var observations = new List<Observation>();
        var orderCount = patientResult.ORDER_OBSERVATIONRepetitionsUsed;

        DiagnosticReport? report = null;

        for (int o = 0; o < orderCount; o++)
        {
            var orderObs = patientResult.GetORDER_OBSERVATION(o);

            // First order produces the DiagnosticReport (covers OBR)
            if (report == null)
            {
                report = BuildDiagnosticReport(orderObs.OBR, patient);
            }

            // Each OBSERVATION wraps an OBX segment
            var obxCount = orderObs.OBSERVATIONRepetitionsUsed;
            for (int i = 0; i < obxCount; i++)
            {
                var obx = orderObs.GetOBSERVATION(i).OBX;
                var obs = BuildObservation(obx, patient);
                observations.Add(obs);

                // Link the observation to the report
                report.Result.Add(new ResourceReference($"Observation/{obs.Id}"));
            }
        }

        return BuildBundle(patient, report, observations);
    }

    private static Patient BuildPatient(PID pid)
    {
        // Reuses the same logic as AdtToFhirStrategy — could be refactored into a shared
        // PatientFactory if more strategies are added (DRY principle).
        var patient = new Patient
        {
            Id = Guid.NewGuid().ToString(),
            Active = true
        };

        var mrn = pid.GetPatientIdentifierList(0).IDNumber.Value;
        if (!string.IsNullOrEmpty(mrn))
        {
            patient.Identifier.Add(new Identifier
            {
                Use = Identifier.IdentifierUse.Usual,
                System = "http://hospital.healthbridge.local/mrn",
                Value = mrn,
                Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MR", "Medical Record Number")
            });
        }

        var name = pid.GetPatientName(0);
        patient.Name.Add(new HumanName
        {
            Use = HumanName.NameUse.Official,
            Family = name.FamilyName.Surname.Value ?? "",
            Given = new[] { name.GivenName.Value ?? "" }
        });

        var dob = pid.DateTimeOfBirth.Time.Value;
        if (!string.IsNullOrEmpty(dob) && dob.Length >= 8)
        {
            patient.BirthDate = $"{dob.Substring(0, 4)}-{dob.Substring(4, 2)}-{dob.Substring(6, 2)}";
        }

        patient.Gender = pid.AdministrativeSex.Value switch
        {
            "M" => AdministrativeGender.Male,
            "F" => AdministrativeGender.Female,
            "O" => AdministrativeGender.Other,
            _   => AdministrativeGender.Unknown
        };

        return patient;
    }

    private static DiagnosticReport BuildDiagnosticReport(OBR obr, Patient patient)
    {
        var report = new DiagnosticReport
        {
            Id = Guid.NewGuid().ToString(),
            Status = DiagnosticReport.DiagnosticReportStatus.Final,
            Subject = new ResourceReference($"Patient/{patient.Id}")
        };

        // OBR-4 — Universal Service Identifier (the test that was ordered), a CE data type.
        //   https://hl7-definition.caristix.com/v2/HL7v2.5/DataTypes/CE
        // We assume LOINC below; OBR-4.3 is the component that actually names the coding system.
        var serviceId = obr.UniversalServiceIdentifier;
        var code = serviceId.Identifier.Value ?? "";
        var display = serviceId.Text.Value ?? "";

        report.Code = new CodeableConcept
        {
            Coding = new List<Coding>
            {
                new("http://loinc.org", code, display)
            },
            Text = display
        };

        // Category — laboratory by default for ORU. "LAB" is HL7 table 0074 (diagnostic
        // service section ID); radiology ORUs would be "RAD", so this default is only
        // correct for lab feeds. https://terminology.hl7.org/CodeSystem-v2-0074.html
        report.Category.Add(new CodeableConcept(
            "http://terminology.hl7.org/CodeSystem/v2-0074", "LAB", "Laboratory"));

        return report;
    }

    private static Observation BuildObservation(OBX obx, Patient patient)
    {
        var obs = new Observation
        {
            Id = Guid.NewGuid().ToString(),
            Status = ObservationStatus.Final,
            Subject = new ResourceReference($"Patient/{patient.Id}")
        };

        // Category — laboratory. Value set bound to Observation.category:
        //   https://hl7.org/fhir/R4/valueset-observation-category.html
        obs.Category.Add(new CodeableConcept(
            "http://terminology.hl7.org/CodeSystem/observation-category", "laboratory", "Laboratory"));

        // OBX-3 — Observation identifier (e.g. WBC^White Blood Cell Count), a CE data type.
        // Labelled as LOINC (https://loinc.org/) without verifying OBX-3.3 — see docs/STANDARDS.md §6.
        var obsId = obx.ObservationIdentifier;
        obs.Code = new CodeableConcept
        {
            Coding = new List<Coding>
            {
                new("http://loinc.org", obsId.Identifier.Value ?? "", obsId.Text.Value ?? "")
            },
            Text = obsId.Text.Value ?? ""
        };

        // OBX-5 — Observation value (numeric for NM type)
        // OBX-6 — Units, a CE that should carry UCUM (https://ucum.org/ucum) but often does not.
        // OBX-2 value types other than NM (ST, CE, SN, TX) are legal and are dropped here:
        //   https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70125
        if (obx.ValueType.Value == "NM" && obx.ObservationValueRepetitionsUsed > 0)
        {
            var rawValue = obx.GetObservationValue(0).Data?.ToString() ?? "";
            if (decimal.TryParse(rawValue, out var numericValue))
            {
                obs.Value = new Quantity
                {
                    Value = numericValue,
                    Unit = obx.Units.Identifier.Value ?? "",
                    System = "http://unitsofmeasure.org"
                };
            }
        }

        // OBX-7 — Reference range
        var refRange = obx.ReferencesRange.Value;
        if (!string.IsNullOrEmpty(refRange))
        {
            obs.ReferenceRange.Add(new Observation.ReferenceRangeComponent
            {
                Text = refRange
            });
        }

        // OBX-8 — Abnormal flags, HL7 table 0078 → v3 ObservationInterpretation.
        //   Source value set: https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70078
        //   Target code system: https://terminology.hl7.org/CodeSystem-v3-ObservationInterpretation.html
        //   The two are close but not identical — confirm any code before adding it below.
        if (obx.AbnormalFlagsRepetitionsUsed > 0)
        {
            var flag = obx.GetAbnormalFlags(0).Value;
            if (!string.IsNullOrEmpty(flag))
            {
                obs.Interpretation.Add(new CodeableConcept(
                    "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation",
                    flag,
                    InterpretationDisplay(flag)));
            }
        }

        return obs;
    }

    private static string InterpretationDisplay(string flag) => flag switch
    {
        "N" => "Normal",
        "H" => "High",
        "L" => "Low",
        "A" => "Abnormal",
        "HH" => "Critical High",
        "LL" => "Critical Low",
        _ => flag
    };

    private static Bundle BuildBundle(Patient patient, DiagnosticReport? report, List<Observation> observations)
    {
        var bundle = new Bundle
        {
            Id = Guid.NewGuid().ToString(),
            Type = Bundle.BundleType.Collection,
            Timestamp = DateTimeOffset.UtcNow
        };

        bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = $"urn:uuid:{patient.Id}",
            Resource = patient
        });

        if (report != null)
        {
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"urn:uuid:{report.Id}",
                Resource = report
            });
        }

        foreach (var obs in observations)
        {
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"urn:uuid:{obs.Id}",
                Resource = obs
            });
        }

        return bundle;
    }
}
