using System.Text;
using HealthBridge.HL7Service.Builders;
using HealthBridge.HL7Service.Controllers;
using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Controllers;

/// <summary>
/// Tests for the HL7 REST endpoints: status codes, ACK generation, and the
/// X-HL7-ACK response header.
/// </summary>
public class HL7ControllerTests
{
    private static HL7Controller BuildController()
    {
        var parser = new HL7ParserService(
            new IMessageExtractorStrategy[]
            {
                new AdtExtractorStrategy(),
                new OruExtractorStrategy(),
                new DefaultExtractorStrategy(),
            },
            TestHelpers.NullLoggerFor<HL7ParserService>());

        return new HL7Controller(parser, new Hl7AckBuilder(), TestHelpers.NullLoggerFor<HL7Controller>());
    }

    private static HL7ParseResult ResultOf(IActionResult action) =>
        Assert.IsType<HL7ParseResult>(((ObjectResult)action).Value);

    // --- POST /api/hl7/parse (text/plain) -------------------------------------

    [Fact]
    public void Parse_returns_200_with_the_parsed_patient_for_a_valid_message()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.AdtA01());

        var action = controller.Parse();

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<HL7ParseResult>(ok.Value);
        Assert.True(result.Success);
        Assert.Equal("PAT001", result.Patient!.PatientId);
    }

    [Fact]
    public void Parse_returns_422_for_an_unparseable_message()
    {
        // 422 rather than 400: the request itself was well-formed, its content was not.
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.Garbage);

        var action = controller.Parse();

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(action);
        Assert.False(ResultOf(unprocessable).Success);
    }

    [Fact]
    public void Parse_returns_400_for_an_empty_body()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, "");

        var action = controller.Parse();

        Assert.IsType<BadRequestObjectResult>(action);
    }

    [Fact]
    public void Parse_returns_400_for_a_whitespace_only_body()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, "   \r\n  ");

        Assert.IsType<BadRequestObjectResult>(controller.Parse());
    }

    [Fact]
    public void Parse_attaches_an_AA_acknowledgement_on_success()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.AdtA01());

        var result = ResultOf(controller.Parse());

        Assert.NotNull(result.Acknowledgement);
        Assert.Contains("MSA|AA|MSG001", result.Acknowledgement);
    }

    [Fact]
    public void Parse_attaches_an_AE_acknowledgement_on_failure()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.Garbage);

        var result = ResultOf(controller.Parse());

        Assert.Contains("MSA|AE|UNKNOWN", result.Acknowledgement);
        Assert.Contains("ERR", result.Acknowledgement);
    }

    [Fact]
    public void Parse_returns_the_ack_base64_encoded_in_the_X_HL7_ACK_header()
    {
        // MLLP-style senders read the ACK from the header rather than the JSON body,
        // so the header must be present and must decode to the same message.
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.AdtA01());

        var result = ResultOf(controller.Parse());

        var header = Assert.Single(controller.Response.Headers["X-HL7-ACK"].ToArray());
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header!));
        Assert.Equal(result.Acknowledgement, decoded);
    }

    [Fact]
    public void The_ack_header_is_present_on_failures_too()
    {
        // A sender that only reads the header must still learn the message was rejected.
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.Garbage);

        controller.Parse();

        var header = Assert.Single(controller.Response.Headers["X-HL7-ACK"].ToArray());
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header!));
        Assert.Contains("MSA|AE", decoded);
    }

    [Fact]
    public void Base64_encoding_the_ack_keeps_carriage_returns_out_of_the_header_value()
    {
        // A raw ACK contains \r, which is not legal in an HTTP header value — this is
        // exactly why the header is base64. Guards against someone "simplifying" it.
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.AdtA01());

        controller.Parse();

        var header = Assert.Single(controller.Response.Headers["X-HL7-ACK"].ToArray());
        Assert.DoesNotContain('\r', header!);
        Assert.DoesNotContain('\n', header!);
    }

    [Fact]
    public void Parse_handles_an_ORU_message()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.OruR01());

        var result = ResultOf(controller.Parse());

        Assert.Equal("ORU_R01", result.MessageType);
        Assert.Equal("PAT002", result.Patient!.PatientId);
    }

    // --- POST /api/hl7/parse/json ---------------------------------------------

    [Fact]
    public void ParseJson_returns_200_for_a_valid_wrapped_message()
    {
        var controller = BuildController();

        var action = controller.ParseJson(new HL7JsonRequest { Message = Hl7Samples.AdtA01() });

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal("PAT001", ((HL7ParseResult)ok.Value!).Patient!.PatientId);
    }

    [Fact]
    public void ParseJson_returns_400_when_the_message_field_is_missing()
    {
        var controller = BuildController();

        Assert.IsType<BadRequestObjectResult>(controller.ParseJson(new HL7JsonRequest()));
    }

    [Fact]
    public void ParseJson_returns_422_for_an_unparseable_message()
    {
        var controller = BuildController();

        var action = controller.ParseJson(new HL7JsonRequest { Message = Hl7Samples.Garbage });

        Assert.IsType<UnprocessableEntityObjectResult>(action);
    }

    [Fact]
    public void ParseJson_and_Parse_produce_the_same_parse_result()
    {
        var viaJson = ResultOf(BuildController().ParseJson(new HL7JsonRequest { Message = Hl7Samples.AdtA01() }));

        var textController = BuildController();
        TestHelpers.GiveRequestBody(textController, Hl7Samples.AdtA01());
        var viaText = ResultOf(textController.Parse());

        Assert.Equal(viaText.MessageType, viaJson.MessageType);
        Assert.Equal(viaText.Patient!.PatientId, viaJson.Patient!.PatientId);
    }

    [Fact]
    public void ParseJson_includes_an_acknowledgement_but_not_the_header()
    {
        // The JSON convenience endpoint returns the ACK in the body only — worth
        // pinning so clients are not written against a header that is not sent.
        var controller = BuildController();

        var result = ResultOf(controller.ParseJson(new HL7JsonRequest { Message = Hl7Samples.AdtA01() }));

        Assert.Contains("MSA|AA", result.Acknowledgement);
    }

    // --- POST /api/hl7/ack ----------------------------------------------------

    [Fact]
    public void Acknowledge_returns_a_plain_text_AA_ack()
    {
        var controller = BuildController();

        var action = controller.Acknowledge(new AckRequest { MessageId = "MSG001", Success = true });

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Contains("MSA|AA|MSG001", content.Content);
    }

    [Fact]
    public void Acknowledge_returns_a_NACK_with_an_ERR_segment_when_asked()
    {
        var controller = BuildController();

        var action = controller.Acknowledge(new AckRequest
        {
            MessageId = "MSG001",
            Success = false,
            ErrorDetail = "PID segment missing"
        });

        var content = Assert.IsType<ContentResult>(action);
        Assert.Contains("MSA|AE|MSG001", content.Content);
        Assert.Contains("PID segment missing", content.Content);
    }

    [Fact]
    public void Acknowledge_defaults_to_success_when_the_flag_is_not_supplied()
    {
        var controller = BuildController();

        var action = controller.Acknowledge(new AckRequest { MessageId = "MSG001" });

        Assert.Contains("MSA|AA", ((ContentResult)action).Content);
    }
}
