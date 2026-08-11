using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using NHapi.Base.Model;
using NHapi.Base.Parser;
using NHapi.Model.V25.Message;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Strategies;

/// <summary>
/// Tests for the HL7 v2 → PatientData extraction strategies.
///
/// Field positions verified against HL7 v2.5:
///   PID — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PID
///   PV1 — https://hl7-definition.caristix.com/v2/HL7v2.5/Segments/PV1
/// </summary>
public class ExtractorStrategyTests
{
    private static readonly PipeParser Parser = new();

    private static IMessage Parse(string raw) => Parser.Parse(raw);

    // --- AdtExtractorStrategy -------------------------------------------------

    [Fact]
    public void Adt_strategy_handles_ADT_A01_and_nothing_else()
    {
        var strategy = new AdtExtractorStrategy();

        Assert.True(strategy.CanHandle(Parse(Hl7Samples.AdtA01())));
        Assert.False(strategy.CanHandle(Parse(Hl7Samples.OruR01())));
    }

    [Fact]
    public void Adt_strategy_extracts_every_mapped_field()
    {
        var strategy = new AdtExtractorStrategy();

        var patient = strategy.Extract(Parse(Hl7Samples.AdtA01()));

        Assert.Equal("MSG001", patient.MessageId);        // MSH-10
        Assert.Equal("HospitalEMR", patient.SendingApp);  // MSH-3
        Assert.Equal("PAT001", patient.PatientId);        // PID-3
        Assert.Equal("John", patient.FirstName);          // PID-5.2
        Assert.Equal("Smith", patient.LastName);          // PID-5.1
        Assert.Equal("19800315", patient.DateOfBirth);    // PID-7
        Assert.Equal("M", patient.Gender);                // PID-8
        Assert.Equal("101", patient.Ward);                // PV1-3.2 (room, not point of care)
        Assert.Equal("E", patient.AdmissionType);         // PV1-4
    }

    [Fact]
    public void Adt_strategy_reads_the_room_component_of_PV1_3_not_the_point_of_care()
    {
        // PV1-3 is a PL: point-of-care ^ room ^ bed. The sample sends ICU^101^A,
        // so "Ward" must be 101 — mislabelled but that is what the field holds.
        // https://hl7-definition.caristix.com/v2/HL7v2.5/DataTypes/PL
        var patient = new AdtExtractorStrategy().Extract(Parse(Hl7Samples.AdtA01()));

        Assert.Equal("101", patient.Ward);
        Assert.NotEqual("ICU", patient.Ward);
    }

    [Fact]
    public void Adt_strategy_returns_empty_strings_for_absent_optional_fields()
    {
        // A minimal but legal A01: no room, no admission type. Extraction must not throw,
        // and must not produce nulls — PatientData is serialized straight to JSON.
        var patient = new AdtExtractorStrategy().Extract(Parse(Hl7Samples.AdtA01Minimal()));

        Assert.Equal("PAT999", patient.PatientId);
        Assert.Equal("", patient.Ward);
        Assert.Equal("", patient.AdmissionType);
    }

    [Fact]
    public void Adt_strategy_also_handles_A08_updates()
    {
        // nHapi maps A08 onto the ADT_A01 structure in v2.5, which is what makes the
        // single strategy sufficient. If nHapi ever stops doing that, this fails loudly.
        var message = Parse(Hl7Samples.AdtA08());
        var strategy = new AdtExtractorStrategy();

        Assert.True(strategy.CanHandle(message));

        var patient = strategy.Extract(message);
        Assert.Equal("PAT002", patient.PatientId);
        Assert.Equal("Garcia", patient.LastName);
        Assert.Equal("205", patient.Ward);
    }

    // --- OruExtractorStrategy -------------------------------------------------

    [Fact]
    public void Oru_strategy_handles_ORU_R01_and_nothing_else()
    {
        var strategy = new OruExtractorStrategy();

        Assert.True(strategy.CanHandle(Parse(Hl7Samples.OruR01())));
        Assert.False(strategy.CanHandle(Parse(Hl7Samples.AdtA01())));
    }

    [Fact]
    public void Oru_strategy_reaches_PID_through_the_nested_PATIENT_RESULT_group()
    {
        // ORU nests PID under PATIENT_RESULT → PATIENT rather than at the top level.
        // Getting this traversal wrong yields empty demographics, not an exception,
        // which is exactly why it is worth pinning.
        var patient = new OruExtractorStrategy().Extract(Parse(Hl7Samples.OruR01()));

        Assert.Equal("MSG002", patient.MessageId);
        Assert.Equal("LabSystem", patient.SendingApp);
        Assert.Equal("PAT002", patient.PatientId);
        Assert.Equal("Jane", patient.FirstName);
        Assert.Equal("Doe", patient.LastName);
        Assert.Equal("19920720", patient.DateOfBirth);
        Assert.Equal("F", patient.Gender);
    }

    [Fact]
    public void Oru_strategy_leaves_visit_fields_empty__ORU_carries_no_PV1()
    {
        var patient = new OruExtractorStrategy().Extract(Parse(Hl7Samples.OruR01()));

        Assert.Equal("", patient.Ward);
        Assert.Equal("", patient.AdmissionType);
    }

    // --- DefaultExtractorStrategy ---------------------------------------------

    [Fact]
    public void Default_strategy_claims_every_message()
    {
        var strategy = new DefaultExtractorStrategy();

        Assert.True(strategy.CanHandle(Parse(Hl7Samples.AdtA01())));
        Assert.True(strategy.CanHandle(Parse(Hl7Samples.OruR01())));
        Assert.True(strategy.CanHandle(Parse(Hl7Samples.SiuS12())));
    }

    [Fact]
    public void Default_strategy_extracts_MSH_fields_from_an_unsupported_message_type()
    {
        // SIU^S12 has no dedicated extractor. MSH is mandatory in every HL7 message,
        // so the catch-all can still report who sent what.
        var patient = new DefaultExtractorStrategy().Extract(Parse(Hl7Samples.SiuS12()));

        Assert.Equal("MSG005", patient.MessageId);
        Assert.Equal("SchedApp", patient.SendingApp);
        Assert.Equal("", patient.PatientId);   // no PID read on this path
    }

    // --- Registration order ---------------------------------------------------

    [Fact]
    public void Default_strategy_must_be_registered_last_or_it_shadows_the_others()
    {
        // Mirrors the DI registration order in Program.cs. FirstOrDefault(CanHandle)
        // means a catch-all registered early would swallow every message, and the
        // failure would be silent: demographics simply stop appearing.
        var strategies = new IMessageExtractorStrategy[]
        {
            new AdtExtractorStrategy(),
            new OruExtractorStrategy(),
            new DefaultExtractorStrategy(),
        };

        var adt = Parse(Hl7Samples.AdtA01());
        var selected = strategies.First(s => s.CanHandle(adt));

        Assert.IsType<AdtExtractorStrategy>(selected);
    }

    [Fact]
    public void Adt_and_Oru_strategies_never_both_claim_the_same_message()
    {
        var adtStrategy = new AdtExtractorStrategy();
        var oruStrategy = new OruExtractorStrategy();

        foreach (var raw in new[] { Hl7Samples.AdtA01(), Hl7Samples.AdtA08(), Hl7Samples.OruR01() })
        {
            var message = Parse(raw);
            Assert.False(adtStrategy.CanHandle(message) && oruStrategy.CanHandle(message));
        }
    }

    [Fact]
    public void Parsed_sample_types_are_what_the_strategies_expect()
    {
        // Guards the fixtures themselves: if a sample stops parsing to the expected
        // nHapi type, the strategy tests above would pass vacuously.
        Assert.IsType<ADT_A01>(Parse(Hl7Samples.AdtA01()));
        Assert.IsType<ORU_R01>(Parse(Hl7Samples.OruR01()));
    }
}
