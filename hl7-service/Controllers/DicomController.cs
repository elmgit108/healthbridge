using Microsoft.AspNetCore.Mvc;
using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Models;

namespace HealthBridge.HL7Service.Controllers;

/// <summary>
/// REST API for parsing DICOM medical imaging metadata.
///
/// DICOM files (.dcm) contain both the image pixels and structured metadata
/// (patient name, study date, modality, institution, etc.). This controller
/// exposes two ways to submit DICOM data:
///   - File upload: parse a real .dcm binary file
///   - JSON input: submit metadata directly (for testing without real images)
///
/// Sources: DICOM PS3 (NEMA, normative) —
///   https://dicom.nema.org/medical/dicom/current/output/chtml/
/// Tag-level references are on the DicomMetadata model; see also docs/STANDARDS.md §2.
///
/// Note: this is a plain REST API, not DICOMweb. If interop with PACS/VNA tooling is ever
/// needed, the standard web API is PS3.18 (STOW-RS / WADO-RS / QIDO-RS):
///   https://dicom.nema.org/medical/dicom/current/output/chtml/part18/PS3.18.html
/// </summary>
[ApiController]
[Route("api/dicom")]
public class DicomController : ControllerBase
{
    private readonly IDicomService _dicom;
    private readonly ILogger<DicomController> _logger;

    public DicomController(IDicomService dicom, ILogger<DicomController> logger)
    {
        _dicom = dicom;
        _logger = logger;
    }

    /// <summary>
    /// Upload a .dcm file and extract its metadata tags.
    /// Uses fo-dicom to read the binary DICOM format.
    /// </summary>
    [HttpPost("parse")]
    [Produces(ContentTypes.Json)]
    public async Task<IActionResult> ParseFile(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "A .dcm file is required" });

        if (!file.FileName.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "File must have .dcm extension" });

        try
        {
            await using var stream = file.OpenReadStream();
            var metadata = _dicom.ExtractMetadata(stream);
            return Ok(metadata);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Submit DICOM metadata as JSON — no .dcm file needed.
    /// Useful for demos, integration testing, and frontend development.
    /// </summary>
    [HttpPost("metadata")]
    [Produces(ContentTypes.Json)]
    public IActionResult SubmitMetadata([FromBody] DicomJsonInput input)
    {
        var metadata = _dicom.ExtractFromJson(input);
        return Ok(metadata);
    }
}

/// <summary>
/// Health check endpoint — polled by the Go gateway and Python monitoring service
/// to determine if this service is alive.
/// </summary>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new HealthStatus());
}
