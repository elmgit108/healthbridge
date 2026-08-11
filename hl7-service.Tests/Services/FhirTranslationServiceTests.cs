using System.Text.Json;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Hl7.Fhir.Model;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Services;

/// <summary>
/// Tests for the FHIR translation orchestrator: strategy selection, error handling,
/// and JSON serialization.
/// </summary>
public class FhirTranslationServiceTests
{
    private static FhirTranslationService BuildService() => new(
        new IFhirTranslatorStrategy[]
        {
            new AdtToFhirStrategy(),
            new OruToFhirStrategy(),
        },
        TestHelpers.NullLoggerFor<FhirTranslationService>());

    [Fact]
    public void Routes_an_ADT_message_to_the_ADT_strategy()
    {
        var bundle = BuildService().Translate(Hl7Samples.AdtA01());

        Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<Encounter>());
        Assert.Empty(bundle.Entry.Select(e => e.Resource).OfType<Observation>());
    }

    [Fact]
    public void Routes_an_ORU_message_to_the_ORU_strategy()
    {
        var bundle = BuildService().Translate(Hl7Samples.OruR01());

        Assert.Single(bundle.Entry.Select(e => e.Resource).OfType<DiagnosticReport>());
        Assert.Empty(bundle.Entry.Select(e => e.Resource).OfType<Encounter>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Blank_input_raises_ArgumentException(string input)
    {
        var service = BuildService();

        Assert.Throws<ArgumentException>(() => service.Translate(input));
    }

    [Fact]
    public void An_unsupported_message_type_raises_NotSupportedException()
    {
        // The controller maps this to 422 with an explanatory message; any other
        // exception type becomes an opaque 500.
        var service = BuildService();

        var ex = Assert.Throws<NotSupportedException>(() => service.Translate(Hl7Samples.SiuS12()));
        Assert.Contains("SIU_S12", ex.Message);
    }

    [Fact]
    public void Unparseable_input_propagates_the_parser_failure()
    {
        // Unlike the parse endpoint, translation has no ACK path — the exception
        // surfaces and the controller turns it into a 422.
        var service = BuildService();

        Assert.ThrowsAny<Exception>(() => service.Translate(Hl7Samples.Garbage));
    }

    [Fact]
    public void Accepts_unix_line_endings()
    {
        var message = Hl7Samples.AdtA01().Replace("\r", "\n");

        var bundle = BuildService().Translate(message);

        Assert.NotEmpty(bundle.Entry);
    }

    [Fact]
    public void TranslateToJson_emits_parseable_FHIR_json()
    {
        var json = BuildService().TranslateToJson(Hl7Samples.AdtA01());

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Bundle", document.RootElement.GetProperty("resourceType").GetString());
        Assert.Equal("collection", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("entry").GetArrayLength());
    }

    [Fact]
    public void Serialized_json_contains_the_patient_identifier_and_name()
    {
        var json = BuildService().TranslateToJson(Hl7Samples.AdtA01());

        Assert.Contains("PAT001", json);
        Assert.Contains("Smith", json);
        Assert.Contains("1980-03-15", json);
    }

    [Fact]
    public void Serialized_json_uses_the_official_terminology_system_urls()
    {
        // A typo in a system URL produces JSON that still parses but that no receiver
        // can bind — worth asserting on the wire format, not just the object model.
        var json = BuildService().TranslateToJson(Hl7Samples.AdtA01());

        Assert.Contains("http://terminology.hl7.org/CodeSystem/v2-0203", json);
        Assert.Contains("http://terminology.hl7.org/CodeSystem/v3-ActCode", json);
    }

    [Fact]
    public void Serialized_ORU_json_carries_UCUM_and_LOINC_systems()
    {
        var json = BuildService().TranslateToJson(Hl7Samples.OruR01());

        Assert.Contains("http://unitsofmeasure.org", json);
        Assert.Contains("http://loinc.org", json);
    }

    [Fact]
    public void No_registered_strategies_means_every_message_is_unsupported()
    {
        var service = new FhirTranslationService(
            Array.Empty<IFhirTranslatorStrategy>(),
            TestHelpers.NullLoggerFor<FhirTranslationService>());

        Assert.Throws<NotSupportedException>(() => service.Translate(Hl7Samples.AdtA01()));
    }

    [Fact]
    public void Concurrent_translations_stay_independent()
    {
        // Registered as a singleton, so the shared PipeParser and serializer are
        // exercised in parallel by real traffic.
        var service = BuildService();

        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 30, i =>
        {
            var raw = i % 2 == 0 ? Hl7Samples.AdtA01() : Hl7Samples.OruR01();
            results.Add(service.TranslateToJson(raw));
        });

        Assert.Equal(30, results.Count);
        Assert.Equal(15, results.Count(j => j.Contains("Encounter")));
        Assert.Equal(15, results.Count(j => j.Contains("DiagnosticReport")));
    }
}
