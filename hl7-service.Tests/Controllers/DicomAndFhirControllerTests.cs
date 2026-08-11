using System.Text;
using System.Text.Json;
using HealthBridge.HL7Service.Controllers;
using HealthBridge.HL7Service.Models;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Tests.TestData;
using HealthBridge.HL7Service.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace HealthBridge.HL7Service.Tests.Controllers;

/// <summary>Tests for the DICOM upload/metadata endpoints and the health endpoint.</summary>
public class DicomControllerTests
{
    private static DicomController BuildController() => new(
        new DicomService(TestHelpers.NullLoggerFor<DicomService>()),
        TestHelpers.NullLoggerFor<DicomController>());

    /// <summary>Wraps a stream as an uploaded file, the way model binding would.</summary>
    private static IFormFile AsUpload(Stream content, string fileName) =>
        new FormFile(content, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/dicom"
        };

    [Fact]
    public async Task ParseFile_returns_200_with_metadata_for_a_valid_dcm()
    {
        using var dcm = DicomSamples.FullCtStudy();
        var action = await BuildController().ParseFile(AsUpload(dcm, "study.dcm"));

        var ok = Assert.IsType<OkObjectResult>(action);
        var metadata = Assert.IsType<DicomMetadata>(ok.Value);
        Assert.Equal("CT", metadata.Modality);
        Assert.Equal(DicomSamples.PatientId, metadata.PatientId);
        Assert.Equal(512, metadata.Rows);
    }

    [Fact]
    public async Task ParseFile_returns_400_when_no_file_is_supplied()
    {
        var action = await BuildController().ParseFile(null!);

        Assert.IsType<BadRequestObjectResult>(action);
    }

    [Fact]
    public async Task ParseFile_returns_400_for_an_empty_file()
    {
        var action = await BuildController().ParseFile(AsUpload(new MemoryStream(), "empty.dcm"));

        Assert.IsType<BadRequestObjectResult>(action);
    }

    [Fact]
    public async Task ParseFile_rejects_files_without_a_dcm_extension()
    {
        using var dcm = DicomSamples.FullCtStudy();

        var action = await BuildController().ParseFile(AsUpload(dcm, "study.jpg"));

        Assert.IsType<BadRequestObjectResult>(action);
    }

    [Fact]
    public async Task ParseFile_accepts_an_uppercase_extension()
    {
        // The extension check is case-insensitive; PACS exports frequently use .DCM.
        using var dcm = DicomSamples.FullCtStudy();

        var action = await BuildController().ParseFile(AsUpload(dcm, "STUDY.DCM"));

        Assert.IsType<OkObjectResult>(action);
    }

    [Fact]
    public async Task ParseFile_returns_422_when_the_bytes_are_not_DICOM()
    {
        // Correct extension, wrong content — the failure has to come from parsing,
        // not from the filename check.
        using var notDicom = DicomSamples.NotADicomFile();

        var action = await BuildController().ParseFile(AsUpload(notDicom, "pretend.dcm"));

        Assert.IsType<UnprocessableEntityObjectResult>(action);
    }

    [Fact]
    public void SubmitMetadata_returns_200_with_the_mapped_metadata()
    {
        var action = BuildController().SubmitMetadata(new DicomJsonInput
        {
            PatientName = "Doe^Jane",
            PatientId = "PAT002",
            Modality = "MR"
        });

        var ok = Assert.IsType<OkObjectResult>(action);
        var metadata = Assert.IsType<DicomMetadata>(ok.Value);
        Assert.Equal("Doe^Jane", metadata.PatientName);
        Assert.Equal("MR", metadata.Modality);
    }

    [Fact]
    public void SubmitMetadata_generates_a_study_uid_when_one_is_not_supplied()
    {
        var action = BuildController().SubmitMetadata(new DicomJsonInput { Modality = "US" });

        var metadata = Assert.IsType<DicomMetadata>(((OkObjectResult)action).Value);
        Assert.NotEmpty(metadata.StudyInstanceUid);
    }
}

/// <summary>Tests for the FHIR translation endpoints.</summary>
public class FhirControllerTests
{
    private static FhirController BuildController()
    {
        var translator = new FhirTranslationService(
            new IFhirTranslatorStrategy[] { new AdtToFhirStrategy(), new OruToFhirStrategy() },
            TestHelpers.NullLoggerFor<FhirTranslationService>());

        return new FhirController(translator, TestHelpers.NullLoggerFor<FhirController>());
    }

    [Fact]
    public async Task Translate_returns_a_fhir_json_bundle()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.AdtA01());

        var action = await controller.Translate();

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("application/fhir+json", content.ContentType);

        using var document = JsonDocument.Parse(content.Content!);
        Assert.Equal("Bundle", document.RootElement.GetProperty("resourceType").GetString());
    }

    [Fact]
    public async Task Translate_returns_400_for_an_empty_body()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, "");

        Assert.IsType<BadRequestObjectResult>(await controller.Translate());
    }

    [Fact]
    public async Task Translate_returns_422_for_an_unsupported_message_type()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.SiuS12());

        Assert.IsType<UnprocessableEntityObjectResult>(await controller.Translate());
    }

    [Fact]
    public async Task Translate_returns_422_for_an_unparseable_message()
    {
        // Falls into the general catch, not the NotSupportedException branch —
        // both must produce 422 rather than an unhandled 500.
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.Garbage);

        Assert.IsType<UnprocessableEntityObjectResult>(await controller.Translate());
    }

    [Fact]
    public async Task Translate_handles_an_ORU_message()
    {
        var controller = BuildController();
        TestHelpers.GiveRequestBody(controller, Hl7Samples.OruR01());

        var content = Assert.IsType<ContentResult>(await controller.Translate());

        Assert.Contains("DiagnosticReport", content.Content);
        Assert.Contains("Observation", content.Content);
    }

    [Fact]
    public void TranslateFromJson_returns_a_bundle_for_a_wrapped_message()
    {
        var action = BuildController().TranslateFromJson(
            new FhirTranslateRequest { Message = Hl7Samples.AdtA01() });

        var content = Assert.IsType<ContentResult>(action);
        Assert.Equal("application/fhir+json", content.ContentType);
        Assert.Contains("PAT001", content.Content);
    }

    [Fact]
    public void TranslateFromJson_returns_400_when_the_message_field_is_missing()
    {
        var action = BuildController().TranslateFromJson(new FhirTranslateRequest());

        Assert.IsType<BadRequestObjectResult>(action);
    }

    [Fact]
    public void TranslateFromJson_returns_422_for_an_unsupported_message_type()
    {
        var action = BuildController().TranslateFromJson(
            new FhirTranslateRequest { Message = Hl7Samples.SiuS12() });

        Assert.IsType<UnprocessableEntityObjectResult>(action);
    }

    [Fact]
    public async Task Both_translate_endpoints_produce_identical_json_for_the_same_input()
    {
        var viaJson = (ContentResult)BuildController()
            .TranslateFromJson(new FhirTranslateRequest { Message = Hl7Samples.OruR01() });

        var textController = BuildController();
        TestHelpers.GiveRequestBody(textController, Hl7Samples.OruR01());
        // await rather than .GetAwaiter().GetResult(): blocking on a task inside a
        // test can deadlock depending on the synchronisation context (xUnit1031).
        var viaText = (ContentResult)await textController.Translate();

        // Resource ids are freshly generated per call, so compare structure, not bytes.
        using var fromJson = JsonDocument.Parse(viaJson.Content!);
        using var fromText = JsonDocument.Parse(viaText.Content!);

        Assert.Equal(
            fromJson.RootElement.GetProperty("entry").GetArrayLength(),
            fromText.RootElement.GetProperty("entry").GetArrayLength());
        Assert.Equal(viaJson.ContentType, viaText.ContentType);
    }
}

/// <summary>Tests for the health endpoint the gateway and monitoring service poll.</summary>
public class HealthControllerTests
{
    [Fact]
    public void Health_returns_200_with_the_agreed_status_shape()
    {
        // The Go gateway and the Python monitoring service both treat a non-200 as
        // "unhealthy", and the dashboard reads these field names.
        var action = new HealthController().Get();

        var ok = Assert.IsType<OkObjectResult>(action);
        var status = Assert.IsType<HealthStatus>(ok.Value);

        Assert.Equal("healthy", status.Status);
        Assert.Equal("HealthBridge HL7/DICOM Service", status.Service);
        Assert.NotEmpty(status.Version);
    }

    [Fact]
    public void Health_timestamp_is_UTC_and_current()
    {
        var status = Assert.IsType<HealthStatus>(((OkObjectResult)new HealthController().Get()).Value);

        Assert.Equal(DateTimeKind.Utc, status.Timestamp.Kind);
        Assert.InRange(status.Timestamp, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }
}
