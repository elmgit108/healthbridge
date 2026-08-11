using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using Hl7.Fhir.Model;
using NHapi.Base.Parser;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Strategies;

/// <summary>
/// Tests for ADT^A01 → FHIR R4 translation.
///
/// Verified against:
///   Patient   — https://hl7.org/fhir/R4/patient.html
///   Encounter — https://hl7.org/fhir/R4/encounter.html
///   v2-0203 identifier types — https://terminology.hl7.org/CodeSystem-v2-0203.html
///   v3-ActCode encounter classes — https://terminology.hl7.org/CodeSystem-v3-ActCode.html
/// </summary>
public class AdtToFhirStrategyTests
{
    private static readonly PipeParser Parser = new();
    private readonly AdtToFhirStrategy _strategy = new();

    private Bundle Translate(string raw) => _strategy.Translate(Parser.Parse(raw));

    private static Patient PatientIn(Bundle bundle) =>
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single();

    private static Encounter EncounterIn(Bundle bundle) =>
        bundle.Entry.Select(e => e.Resource).OfType<Encounter>().Single();

    [Fact]
    public void Handles_ADT_A01_only()
    {
        Assert.True(_strategy.CanHandle(Parser.Parse(Hl7Samples.AdtA01())));
        Assert.False(_strategy.CanHandle(Parser.Parse(Hl7Samples.OruR01())));
    }

    [Fact]
    public void Produces_a_collection_bundle_with_a_Patient_and_an_Encounter()
    {
        var bundle = Translate(Hl7Samples.AdtA01());

        Assert.Equal(Bundle.BundleType.Collection, bundle.Type);
        Assert.Equal(2, bundle.Entry.Count);
        Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<Patient>());
        Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<Encounter>());
    }

    [Fact]
    public void Bundle_entries_use_urn_uuid_fullUrls()
    {
        // R4 requires an absolute fullUrl; resources with no server identity use urn:uuid.
        // https://hl7.org/fhir/R4/bundle.html
        var bundle = Translate(Hl7Samples.AdtA01());

        Assert.All(bundle.Entry, entry => Assert.StartsWith("urn:uuid:", entry.FullUrl));
    }

    [Fact]
    public void PID_3_becomes_a_Patient_identifier_typed_MR()
    {
        var patient = PatientIn(Translate(Hl7Samples.AdtA01()));

        var identifier = Assert.Single(patient.Identifier);
        Assert.Equal("PAT001", identifier.Value);
        Assert.Equal(Identifier.IdentifierUse.Usual, identifier.Use);

        var coding = Assert.Single(identifier.Type.Coding);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v2-0203", coding.System);
        Assert.Equal("MR", coding.Code);
    }

    [Fact]
    public void PID_5_components_map_to_family_and_given_names()
    {
        var patient = PatientIn(Translate(Hl7Samples.AdtA01()));

        var name = Assert.Single(patient.Name);
        Assert.Equal("Smith", name.Family);                   // PID-5.1 → family
        Assert.Equal(new[] { "John" }, name.Given);           // PID-5.2 → given
        Assert.Equal(HumanName.NameUse.Official, name.Use);
    }

    [Fact]
    public void PID_7_is_reformatted_from_HL7_yyyyMMdd_to_FHIR_date()
    {
        // The one real format conversion in this mapping: HL7 TS → FHIR date.
        var patient = PatientIn(Translate(Hl7Samples.AdtA01()));

        Assert.Equal("1980-03-15", patient.BirthDate);
    }

    [Theory]
    [InlineData("M", AdministrativeGender.Male)]
    [InlineData("F", AdministrativeGender.Female)]
    [InlineData("O", AdministrativeGender.Other)]
    [InlineData("U", AdministrativeGender.Unknown)]
    // Table 0001 also defines A (ambiguous) and N (not applicable). FHIR has no direct
    // equivalent, so both land on Unknown — pinned here so the behaviour is deliberate.
    [InlineData("A", AdministrativeGender.Unknown)]
    [InlineData("N", AdministrativeGender.Unknown)]
    public void PID_8_maps_HL7_table_0001_to_FHIR_administrative_gender(string hl7Sex, AdministrativeGender expected)
    {
        var message = Hl7Samples.Join(
            @"MSH|^~\&|EMR|HOSP|HealthBridge|CLOUD|20240115120000||ADT^A01|MSGX|P|2.5",
            "EVN|A01|20240115120000",
            $"PID|1||PATX^^^MRN||Test^Case||19800315|{hl7Sex}",
            "PV1|1|I");

        var patient = PatientIn(Translate(message));

        Assert.Equal(expected, patient.Gender);
    }

    [Fact]
    public void PID_11_address_components_map_in_the_right_order()
    {
        var patient = PatientIn(Translate(Hl7Samples.AdtA01()));

        var address = Assert.Single(patient.Address);
        Assert.Equal(new[] { "123 King St" }, address.Line);
        Assert.Equal("Toronto", address.City);
        Assert.Equal("ON", address.State);
        Assert.Equal("M5H2N2", address.PostalCode);
        Assert.Equal("CA", address.Country);
    }

    [Fact]
    public void Absent_PID_11_produces_no_address_rather_than_an_empty_one()
    {
        var patient = PatientIn(Translate(Hl7Samples.AdtA01Minimal()));

        Assert.Empty(patient.Address);
    }

    [Theory]
    [InlineData("I", "IMP")]
    [InlineData("O", "AMB")]
    [InlineData("E", "EMER")]
    // Table 0004 values we do not map explicitly fall back to ambulatory.
    [InlineData("P", "AMB")]
    [InlineData("R", "AMB")]
    public void PV1_2_maps_HL7_table_0004_to_v3_ActCode(string patientClass, string expectedCode)
    {
        var message = Hl7Samples.Join(
            @"MSH|^~\&|EMR|HOSP|HealthBridge|CLOUD|20240115120000||ADT^A01|MSGX|P|2.5",
            "EVN|A01|20240115120000",
            "PID|1||PATX^^^MRN||Test^Case||19800315|M",
            $"PV1|1|{patientClass}");

        var encounter = EncounterIn(Translate(message));

        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-ActCode", encounter.Class.System);
        Assert.Equal(expectedCode, encounter.Class.Code);
    }

    [Fact]
    public void Encounter_references_the_Patient_in_the_same_bundle()
    {
        // A dangling subject reference is the classic bundle bug — the resources
        // validate individually but the bundle is useless to the receiver.
        var bundle = Translate(Hl7Samples.AdtA01());

        var patient = PatientIn(bundle);
        var encounter = EncounterIn(bundle);

        Assert.Equal($"Patient/{patient.Id}", encounter.Subject.Reference);
    }

    [Fact]
    public void PV1_3_becomes_an_active_encounter_location_combining_point_of_care_and_room()
    {
        var encounter = EncounterIn(Translate(Hl7Samples.AdtA01()));

        var location = Assert.Single(encounter.Location);
        Assert.Equal("ICU 101", location.Location.Display);
        Assert.Equal(Encounter.EncounterLocationStatus.Active, location.Status);
    }

    [Fact]
    public void Absent_PV1_3_produces_no_location_entry()
    {
        var encounter = EncounterIn(Translate(Hl7Samples.AdtA01Minimal()));

        Assert.Empty(encounter.Location);
    }

    [Fact]
    public void PV1_7_attending_doctor_becomes_an_ATND_participant()
    {
        var encounter = EncounterIn(Translate(Hl7Samples.AdtA01()));

        var participant = Assert.Single(encounter.Participant);
        var typeCoding = Assert.Single(Assert.Single(participant.Type).Coding);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", typeCoding.System);
        Assert.Equal("ATND", typeCoding.Code);

        Assert.Equal("Gregory House", participant.Individual.Display);
        Assert.Equal("DOC01", participant.Individual.Identifier.Value);
    }

    [Fact]
    public void Absent_PV1_7_produces_no_participant()
    {
        var encounter = EncounterIn(Translate(Hl7Samples.AdtA01Minimal()));

        Assert.Empty(encounter.Participant);
    }

    [Fact]
    public void Encounter_status_is_populated__it_is_required_by_R4()
    {
        // Encounter.status is 1..1 in R4; an unset status makes the resource invalid.
        var encounter = EncounterIn(Translate(Hl7Samples.AdtA01()));

        Assert.Equal(Encounter.EncounterStatus.InProgress, encounter.Status);
    }

    [Fact]
    public void Every_translation_mints_fresh_resource_ids()
    {
        // Ids come from Guid.NewGuid(), so two translations of the same message must
        // not collide — a receiver treating id as stable would overwrite records.
        var first = Translate(Hl7Samples.AdtA01());
        var second = Translate(Hl7Samples.AdtA01());

        Assert.NotEqual(PatientIn(first).Id, PatientIn(second).Id);
    }
}
