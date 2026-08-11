using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Services;

/// <summary>
/// Tests for the parser service: nHapi parsing, strategy selection, and failure handling.
/// </summary>
public class HL7ParserServiceTests
{
    /// <summary>Builds the service with the same strategy order Program.cs registers.</summary>
    private static HL7ParserService BuildService() => new(
        new IMessageExtractorStrategy[]
        {
            new AdtExtractorStrategy(),
            new OruExtractorStrategy(),
            new DefaultExtractorStrategy(),
        },
        TestHelpers.NullLoggerFor<HL7ParserService>());

    [Fact]
    public void Parses_an_ADT_A01_and_reports_the_structure_name()
    {
        var result = BuildService().ParseMessage(Hl7Samples.AdtA01());

        Assert.True(result.Success);
        Assert.Equal("ADT_A01", result.MessageType);
        Assert.Equal("MSG001", result.MessageId);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Parses_an_ORU_R01_and_extracts_patient_data()
    {
        var result = BuildService().ParseMessage(Hl7Samples.OruR01());

        Assert.True(result.Success);
        Assert.Equal("ORU_R01", result.MessageType);
        Assert.NotNull(result.Patient);
        Assert.Equal("PAT002", result.Patient!.PatientId);
    }

    [Theory]
    [InlineData("\n")]     // Unix line endings
    [InlineData("\r\n")]   // Windows line endings
    [InlineData("\r")]     // The HL7-conformant terminator
    public void Accepts_any_line_ending_by_normalising_to_carriage_return(string lineEnding)
    {
        // HL7 v2.5 Ch. 2.5 mandates \r, but HTTP clients and text editors routinely send
        // \n. Normalization is what makes the HTTP transport usable, so it is load-bearing.
        var message = string.Join(lineEnding,
            @"MSH|^~\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG001|P|2.5",
            "EVN|A01|20240115120000",
            "PID|1||PAT001^^^MRN||Smith^John||19800315|M",
            "PV1|1|I|ICU^101^A|E");

        var result = BuildService().ParseMessage(message);

        Assert.True(result.Success);
        Assert.Equal("PAT001", result.Patient!.PatientId);
    }

    [Fact]
    public void Leading_and_trailing_whitespace_does_not_prevent_parsing()
    {
        var result = BuildService().ParseMessage("\n\n  " + Hl7Samples.AdtA01() + "  \n\n");

        Assert.True(result.Success);
    }

    [Fact]
    public void Garbage_input_fails_without_throwing()
    {
        // The controller turns Success=false into a 422 plus a NACK. An exception
        // escaping here would instead surface as an unhandled 500.
        var result = BuildService().ParseMessage(Hl7Samples.Garbage);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Null(result.Patient);
    }

    [Fact]
    public void A_message_without_MSH_fails()
    {
        var result = BuildService().ParseMessage(Hl7Samples.MissingMsh());

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Empty_input_fails_without_throwing()
    {
        var result = BuildService().ParseMessage("");

        Assert.False(result.Success);
    }

    [Fact]
    public void Message_id_falls_back_to_UNKNOWN_when_no_patient_was_extracted()
    {
        // With no strategies registered nothing extracts MSH-10, and the ACK builder
        // still needs a value for MSA-2.
        var service = new HL7ParserService(
            Array.Empty<IMessageExtractorStrategy>(),
            TestHelpers.NullLoggerFor<HL7ParserService>());

        var result = service.ParseMessage(Hl7Samples.AdtA01());

        Assert.True(result.Success);
        Assert.Equal("UNKNOWN", result.MessageId);
        Assert.Null(result.Patient);
    }

    [Fact]
    public void Selects_the_first_strategy_that_can_handle_the_message()
    {
        var result = BuildService().ParseMessage(Hl7Samples.AdtA01());

        // The ADT strategy reads PV1; the catch-all does not. Ward proves which ran.
        Assert.Equal("101", result.Patient!.Ward);
    }

    [Fact]
    public void Falls_back_to_the_catch_all_strategy_for_unsupported_message_types()
    {
        var result = BuildService().ParseMessage(Hl7Samples.SiuS12());

        Assert.True(result.Success);
        Assert.Equal("MSG005", result.MessageId);
        Assert.Equal("SchedApp", result.Patient!.SendingApp);
        Assert.Equal("", result.Patient.PatientId);
    }

    [Fact]
    public void Parsing_the_same_message_twice_yields_the_same_result()
    {
        // The service is registered as a singleton and shares one PipeParser across
        // concurrent requests, so it must hold no per-message state.
        var service = BuildService();

        var first = service.ParseMessage(Hl7Samples.AdtA01());
        var second = service.ParseMessage(Hl7Samples.AdtA01());

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(first.Patient!.PatientId, second.Patient!.PatientId);
        Assert.Equal(first.Patient.Ward, second.Patient.Ward);
    }

    [Fact]
    public void Interleaved_message_types_do_not_leak_state_between_parses()
    {
        var service = BuildService();

        var adt = service.ParseMessage(Hl7Samples.AdtA01());
        var oru = service.ParseMessage(Hl7Samples.OruR01());
        var adtAgain = service.ParseMessage(Hl7Samples.AdtA01());

        Assert.Equal("101", adt.Patient!.Ward);
        Assert.Equal("", oru.Patient!.Ward);          // ORU has no PV1
        Assert.Equal("101", adtAgain.Patient!.Ward);  // not clobbered by the ORU parse
    }

    [Fact]
    public void Concurrent_parses_return_correct_results_for_each_caller()
    {
        // The singleton registration means real traffic hits this in parallel.
        var service = BuildService();

        var results = new System.Collections.Concurrent.ConcurrentBag<(string Type, string Id)>();
        Parallel.For(0, 50, i =>
        {
            var raw = i % 2 == 0 ? Hl7Samples.AdtA01() : Hl7Samples.OruR01();
            var result = service.ParseMessage(raw);
            results.Add((result.MessageType!, result.MessageId!));
        });

        Assert.Equal(50, results.Count);
        Assert.All(results, r =>
            Assert.Equal(r.Type == "ADT_A01" ? "MSG001" : "MSG002", r.Id));
    }
}
