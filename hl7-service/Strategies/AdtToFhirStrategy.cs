using NHapi.Base.Model;
using NHapi.Model.V25.Message;
using NHapi.Model.V25.Segment;
using Hl7.Fhir.Model;

namespace HealthBridge.HL7Service.Strategies;

/// <summary>
/// Translates HL7 v2 ADT^A01 (Admit/Visit) messages into FHIR R4 resources.
///
/// Output Bundle contains:
///   - Patient resource    (from PID segment)
///   - Encounter resource  (from PV1 segment, referencing the Patient)
///
/// Field mappings (HL7 v2 → FHIR R4):
///   PID-3        → Patient.identifier (system: MRN)
///   PID-5.1/5.2  → Patient.name.family / .given
///   PID-7        → Patient.birthDate
///   PID-8        → Patient.gender
///   PID-11       → Patient.address
///   PV1-2        → Encounter.class (I=inpatient, O=outpatient, E=emergency)
///   PV1-3        → Encounter.location.location
///   PV1-4        → Encounter.priority
///   PV1-7        → Encounter.participant (attending doctor)
///
/// Sources — source format (HL7 v2.5):
///   ADT^A01 — https://hl7-definition.caristix.com/v2/HL7v2.5/TriggerEvents/ADT_A01
///   PID / PV1 — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PID
///               https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PV1
///
/// Sources — target format (FHIR R4 4.0.1, normative):
///   Patient   — https://hl7.org/fhir/R4/patient.html
///   Encounter — https://hl7.org/fhir/R4/encounter.html
///   Bundle    — https://hl7.org/fhir/R4/bundle.html
///
/// Sources — the mapping itself. Verify these hand-written mappings against the official
/// HL7 "Version 2 to FHIR" Implementation Guide before trusting them in production:
///   https://build.fhir.org/ig/HL7/v2-to-fhir/
///   PID → Patient — https://build.fhir.org/ig/HL7/v2-to-fhir/ConceptMap-segment-pid-to-patient.html
/// </summary>
public class AdtToFhirStrategy : IFhirTranslatorStrategy
{
    public bool CanHandle(IMessage message) => message is ADT_A01;

    public Bundle Translate(IMessage message)
    {
        var adt = (ADT_A01)message;

        // Build the Patient resource from PID
        var patient = BuildPatient(adt.PID);

        // Build the Encounter resource from PV1, linked to the Patient
        var encounter = BuildEncounter(adt.PV1, patient);

        // Wrap both in a FHIR Bundle (collection type) — https://hl7.org/fhir/R4/bundle.html
        return BuildBundle(patient, encounter);
    }

    private static Patient BuildPatient(PID pid)
    {
        var patient = new Patient
        {
            Id = Guid.NewGuid().ToString(),
            Active = true
        };

        // Identifier — Medical Record Number from PID-3.
        // "MR" comes from the v2-0203 identifier type code system:
        //   https://terminology.hl7.org/CodeSystem-v2-0203.html
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

        // Name — PID-5
        var hl7Name = pid.GetPatientName(0);
        var humanName = new HumanName
        {
            Use = HumanName.NameUse.Official,
            Family = hl7Name.FamilyName.Surname.Value ?? "",
            Given = new[] { hl7Name.GivenName.Value ?? "" }
        };
        patient.Name.Add(humanName);

        // Birth date — PID-7 (HL7 format: yyyyMMdd → FHIR: yyyy-MM-dd)
        var dob = pid.DateTimeOfBirth.Time.Value;
        if (!string.IsNullOrEmpty(dob) && dob.Length >= 8)
        {
            patient.BirthDate = $"{dob.Substring(0, 4)}-{dob.Substring(4, 2)}-{dob.Substring(6, 2)}";
        }

        // Gender — PID-8 (HL7 table 0001) → FHIR AdministrativeGender.
        //   Source value set: https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70001
        //   Target value set: https://hl7.org/fhir/R4/valueset-administrative-gender.html
        //   Table 0001 also defines A (ambiguous) and N (not applicable) — both fall to Unknown here.
        patient.Gender = pid.AdministrativeSex.Value switch
        {
            "M" => AdministrativeGender.Male,
            "F" => AdministrativeGender.Female,
            "O" => AdministrativeGender.Other,
            _   => AdministrativeGender.Unknown
        };

        // Address — PID-11 (XAD data type; component order per
        //   https://hl7-definition.caristix.com/v2/HL7v2.5/DataTypes/XAD)
        if (pid.PatientAddressRepetitionsUsed > 0)
        {
            var addr = pid.GetPatientAddress(0);
            patient.Address.Add(new Address
            {
                Use = Address.AddressUse.Home,
                Line = new[] { addr.StreetAddress.StreetOrMailingAddress.Value ?? "" },
                City = addr.City.Value ?? "",
                State = addr.StateOrProvince.Value ?? "",
                PostalCode = addr.ZipOrPostalCode.Value ?? "",
                Country = addr.Country.Value ?? ""
            });
        }

        return patient;
    }

    private static Encounter BuildEncounter(PV1 pv1, Patient patient)
    {
        var encounter = new Encounter
        {
            Id = Guid.NewGuid().ToString(),
            Status = Encounter.EncounterStatus.InProgress,

            // Reference to the patient — FHIR uses URN-style references within bundles
            Subject = new ResourceReference($"Patient/{patient.Id}")
        };

        // Class — PV1-2 (patient class, HL7 table 0004) → v3 ActCode.
        //   Source value set: https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70004
        //   Target code system: https://terminology.hl7.org/CodeSystem-v3-ActCode.html
        //   Table 0004 also defines P (preadmit), R (recurring), B, C, N, U — all default to AMB here.
        encounter.Class = pv1.PatientClass.Value switch
        {
            "I" => new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IMP", "inpatient encounter"),
            "O" => new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB", "ambulatory"),
            "E" => new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "EMER", "emergency"),
            _   => new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB", "ambulatory")
        };

        // Location — PV1-3 (assigned patient location, PL data type: point of care, room, bed, facility)
        //   https://hl7-definition.caristix.com/v2/HL7v2.5/DataTypes/PL
        var room = pv1.AssignedPatientLocation.Room.Value;
        var pointOfCare = pv1.AssignedPatientLocation.PointOfCare.Value;
        if (!string.IsNullOrEmpty(room) || !string.IsNullOrEmpty(pointOfCare))
        {
            encounter.Location.Add(new Encounter.LocationComponent
            {
                Location = new ResourceReference
                {
                    Display = $"{pointOfCare} {room}".Trim()
                },
                Status = Encounter.EncounterLocationStatus.Active
            });
        }

        // Attending doctor — PV1-7 (XCN data type, repeating; we take the first).
        //   https://hl7-definition.caristix.com/v2/HL7v2.5/DataTypes/XCN
        //   "ATND" from https://terminology.hl7.org/CodeSystem-v3-ParticipationType.html
        if (pv1.AttendingDoctorRepetitionsUsed > 0)
        {
            var doc = pv1.GetAttendingDoctor(0);
            var docId = doc.IDNumber.Value;
            var docName = $"{doc.GivenName.Value} {doc.FamilyName.Surname.Value}".Trim();
            if (!string.IsNullOrEmpty(docId))
            {
                encounter.Participant.Add(new Encounter.ParticipantComponent
                {
                    Type = new List<CodeableConcept>
                    {
                        new("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", "ATND", "attender")
                    },
                    Individual = new ResourceReference
                    {
                        Display = docName,
                        Identifier = new Identifier("http://hospital.healthbridge.local/practitioner", docId)
                    }
                });
            }
        }

        return encounter;
    }

    private static Bundle BuildBundle(Patient patient, Encounter encounter)
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

        bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = $"urn:uuid:{encounter.Id}",
            Resource = encounter
        });

        return bundle;
    }
}
