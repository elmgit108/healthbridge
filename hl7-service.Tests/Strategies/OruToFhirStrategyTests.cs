using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using Hl7.Fhir.Model;
using NHapi.Base.Parser;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Strategies;

/// <summary>
/// Tests for ORU^R01 → FHIR R4 translation.
///
/// Verified against:
///   Observation      — https://hl7.org/fhir/R4/observation.html
///   DiagnosticReport — https://hl7.org/fhir/R4/diagnosticreport.html
///   OBX-8 flags, table 0078 — https://hl7-definition.caristix.com/v2/HL7v2.5/Tables/HL70078
///   v2-0074 service sections — https://terminology.hl7.org/CodeSystem-v2-0074.html
/// </summary>
public class OruToFhirStrategyTests
{
    private static readonly PipeParser Parser = new();
    private readonly OruToFhirStrategy _strategy = new();

    private Bundle Translate(string raw) => _strategy.Translate(Parser.Parse(raw));

    private static Patient PatientIn(Bundle b) =>
        b.Entry.Select(e => e.Resource).OfType<Patient>().Single();

    private static DiagnosticReport ReportIn(Bundle b) =>
        b.Entry.Select(e => e.Resource).OfType<DiagnosticReport>().Single();

    private static List<Observation> ObservationsIn(Bundle b) =>
        b.Entry.Select(e => e.Resource).OfType<Observation>().ToList();

    [Fact]
    public void Handles_ORU_R01_only()
    {
        Assert.True(_strategy.CanHandle(Parser.Parse(Hl7Samples.OruR01())));
        Assert.False(_strategy.CanHandle(Parser.Parse(Hl7Samples.AdtA01())));
    }

    [Fact]
    public void Produces_Patient_DiagnosticReport_and_one_Observation_per_numeric_OBX()
    {
        var bundle = Translate(Hl7Samples.OruR01());

        Assert.Equal(Bundle.BundleType.Collection, bundle.Type);
        Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<Patient>());
        Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<DiagnosticReport>());
        Assert.Single(ObservationsIn(bundle));
    }

    [Fact]
    public void Patient_demographics_come_from_the_nested_PID()
    {
        var patient = PatientIn(Translate(Hl7Samples.OruR01()));

        Assert.Equal("PAT002", Assert.Single(patient.Identifier).Value);
        Assert.Equal("Doe", Assert.Single(patient.Name).Family);
        Assert.Equal("1992-07-20", patient.BirthDate);
        Assert.Equal(AdministrativeGender.Female, patient.Gender);
    }

    [Fact]
    public void OBR_4_becomes_the_DiagnosticReport_code()
    {
        var report = ReportIn(Translate(Hl7Samples.OruR01()));

        var coding = Assert.Single(report.Code.Coding);
        Assert.Equal("CBC", coding.Code);
        Assert.Equal("Complete Blood Count", coding.Display);
        Assert.Equal("Complete Blood Count", report.Code.Text);
    }

    [Fact]
    public void DiagnosticReport_is_categorised_as_laboratory()
    {
        var report = ReportIn(Translate(Hl7Samples.OruR01()));

        var coding = Assert.Single(Assert.Single(report.Category).Coding);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v2-0074", coding.System);
        Assert.Equal("LAB", coding.Code);
    }

    [Fact]
    public void DiagnosticReport_and_Observations_both_reference_the_Patient()
    {
        var bundle = Translate(Hl7Samples.OruR01());
        var patient = PatientIn(bundle);

        Assert.Equal($"Patient/{patient.Id}", ReportIn(bundle).Subject.Reference);
        Assert.All(ObservationsIn(bundle),
            obs => Assert.Equal($"Patient/{patient.Id}", obs.Subject.Reference));
    }

    [Fact]
    public void DiagnosticReport_result_links_to_every_Observation_it_produced()
    {
        var bundle = Translate(Hl7Samples.OruR01MultipleObservations());
        var report = ReportIn(bundle);
        var observations = ObservationsIn(bundle);

        Assert.Equal(observations.Count, report.Result.Count);
        Assert.Equal(
            observations.Select(o => $"Observation/{o.Id}").OrderBy(x => x),
            report.Result.Select(r => r.Reference).OrderBy(x => x));
    }

    [Fact]
    public void OBX_5_and_OBX_6_become_a_UCUM_quantity()
    {
        var observation = Assert.Single(ObservationsIn(Translate(Hl7Samples.OruR01())));

        var quantity = Assert.IsType<Quantity>(observation.Value);
        Assert.Equal(7.5m, quantity.Value);
        Assert.Equal("10*3/uL", quantity.Unit);
        Assert.Equal("http://unitsofmeasure.org", quantity.System);
    }

    [Fact]
    public void OBX_3_becomes_the_Observation_code()
    {
        var observation = Assert.Single(ObservationsIn(Translate(Hl7Samples.OruR01())));

        var coding = Assert.Single(observation.Code.Coding);
        Assert.Equal("WBC", coding.Code);
        Assert.Equal("White Blood Cell Count", coding.Display);
    }

    [Fact]
    public void OBX_7_reference_range_is_carried_across_as_text()
    {
        var observation = Assert.Single(ObservationsIn(Translate(Hl7Samples.OruR01())));

        Assert.Equal("4.5-11.0", Assert.Single(observation.ReferenceRange).Text);
    }

    [Theory]
    [InlineData("H", "High")]
    [InlineData("L", "Low")]
    [InlineData("N", "Normal")]
    [InlineData("A", "Abnormal")]
    [InlineData("HH", "Critical High")]
    [InlineData("LL", "Critical Low")]
    public void OBX_8_abnormal_flags_map_to_interpretation_codes(string flag, string expectedDisplay)
    {
        var message = Hl7Samples.Join(
            @"MSH|^~\&|Lab|MainLab|HealthBridge|CLOUD|20240115130000||ORU^R01|MSGX|P|2.5",
            "PID|1||PATX^^^MRN||Test^Case||19900101|F",
            "OBR|1|||CBC^Complete Blood Count",
            $"OBX|1|NM|WBC^White Blood Cell Count||7.5|10*3/uL|4.5-11.0|{flag}|||F");

        var interpretation = Assert.Single(ObservationsIn(Translate(message)).Single().Interpretation);

        var coding = Assert.Single(interpretation.Coding);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation", coding.System);
        Assert.Equal(flag, coding.Code);

        // The human-readable label lands on CodeableConcept.text, NOT Coding.display —
        // see Interpretation_display_lands_on_text_not_on_the_coding below.
        Assert.Equal(expectedDisplay, interpretation.Text);
    }

    [Fact]
    public void Interpretation_display_lands_on_text_not_on_the_coding()
    {
        // Firely's CodeableConcept(system, code, text) puts its third argument on
        // CodeableConcept.text; it is Coding(system, code, display) that fills in
        // Coding.display. The strategy uses the former for interpretation and category,
        // and the latter for Observation.code — so Observation.code carries a display
        // while interpretation does not. Valid FHIR either way, but inconsistent, and a
        // consumer reading only coding.display gets nothing for the interpretation.
        //
        //   https://hl7.org/fhir/R4/datatypes.html#CodeableConcept
        var observation = Assert.Single(ObservationsIn(Translate(Hl7Samples.OruR01())));

        var interpretation = Assert.Single(observation.Interpretation);
        Assert.Equal("Normal", interpretation.Text);
        Assert.Null(Assert.Single(interpretation.Coding).Display);

        // For contrast: Observation.code is built with Coding(...), so display is set.
        Assert.Equal("White Blood Cell Count", Assert.Single(observation.Code.Coding).Display);
    }

    [Fact]
    public void Unrecognised_abnormal_flags_pass_through_unchanged()
    {
        // Table 0078 has codes we do not name (>, <, S, R, I, MS, VS). Passing them
        // through unchanged is better than dropping the interpretation entirely.
        var message = Hl7Samples.Join(
            @"MSH|^~\&|Lab|MainLab|HealthBridge|CLOUD|20240115130000||ORU^R01|MSGX|P|2.5",
            "PID|1||PATX^^^MRN||Test^Case||19900101|F",
            "OBR|1|||CBC^Complete Blood Count",
            "OBX|1|NM|WBC^White Blood Cell Count||7.5|10*3/uL|4.5-11.0|S|||F");

        var interpretation = Assert.Single(ObservationsIn(Translate(message)).Single().Interpretation);

        Assert.Equal("S", Assert.Single(interpretation.Coding).Code);
        Assert.Equal("S", interpretation.Text);
    }

    [Fact]
    public void Observations_are_categorised_as_laboratory_and_marked_final()
    {
        var observation = Assert.Single(ObservationsIn(Translate(Hl7Samples.OruR01())));

        Assert.Equal(ObservationStatus.Final, observation.Status);
        var coding = Assert.Single(Assert.Single(observation.Category).Coding);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/observation-category", coding.System);
        Assert.Equal("laboratory", coding.Code);
    }

    [Fact]
    public void Every_OBX_produces_an_Observation_including_non_numeric_ones()
    {
        // The sample carries 3 NM results plus 1 ST comment. An Observation is created
        // per OBX regardless of value type; only the value is conditional.
        var bundle = Translate(Hl7Samples.OruR01MultipleObservations());

        Assert.Equal(4, ObservationsIn(bundle).Count);
    }

    [Fact]
    public void Non_numeric_OBX_yields_an_Observation_with_no_value__documented_gap()
    {
        // Pins the gap in docs/STANDARDS.md §6: OBX-2 = ST is legal HL7, but the result
        // text ("Hemolyzed sample") is silently dropped. The Observation still exists,
        // so a receiver sees a coded result with no value — worse than an explicit omission.
        var bundle = Translate(Hl7Samples.OruR01MultipleObservations());

        var comment = ObservationsIn(bundle)
            .Single(o => o.Code.Coding.Any(c => c.Code == "COMMENT"));

        Assert.Null(comment.Value);
    }

    [Fact]
    public void Multiple_numeric_results_keep_their_own_values_units_and_flags()
    {
        var observations = ObservationsIn(Translate(Hl7Samples.OruR01MultipleObservations()));

        var glucose = observations.Single(o => o.Code.Coding.Any(c => c.Code == "GLU"));
        Assert.Equal(145m, ((Quantity)glucose.Value).Value);
        Assert.Equal("mg/dL", ((Quantity)glucose.Value).Unit);
        Assert.Equal("H", Assert.Single(Assert.Single(glucose.Interpretation).Coding).Code);

        var potassium = observations.Single(o => o.Code.Coding.Any(c => c.Code == "K"));
        Assert.Equal(2.9m, ((Quantity)potassium.Value).Value);
        Assert.Equal("Critical Low", Assert.Single(potassium.Interpretation).Text);
    }
}
