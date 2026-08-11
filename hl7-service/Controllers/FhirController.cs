using Microsoft.AspNetCore.Mvc;
using HealthBridge.HL7Service.Services;

namespace HealthBridge.HL7Service.Controllers;

/// <summary>
/// REST API for translating legacy HL7 v2 messages into modern FHIR R4 resources.
///
/// FHIR (Fast Healthcare Interoperability Resources) is the modern HL7 standard
/// — JSON + REST — used by Epic, Cerner, and mandated by CMS for US healthcare
/// interoperability under the 21st Century Cures Act.
///
/// This endpoint enables legacy hospital systems (which only speak HL7 v2) to
/// integrate with modern FHIR-based EHRs and analytics platforms.
///
/// Sources:
///   FHIR R4 (4.0.1) specification — https://www.hl7.org/fhir/R4/
///   RESTful API + the application/fhir+json media type — https://hl7.org/fhir/R4/http.html
///   HL7 v2 → FHIR mapping IG — https://build.fhir.org/ig/HL7/v2-to-fhir/
///   ONC Cures Act Final Rule (the US interoperability mandate referenced above) —
///     https://www.healthit.gov/topic/oncs-cures-act-final-rule
///   CMS Interoperability and Patient Access rule —
///     https://www.cms.gov/priorities/key-initiatives/burden-reduction/interoperability
/// Resource- and terminology-level references are on the strategies; see docs/STANDARDS.md §3.
///
/// Note: this is a translation endpoint, not a conformant FHIR server — it implements no
/// FHIR RESTful interactions (read/search/create) and publishes no CapabilityStatement.
/// </summary>
[ApiController]
[Route("api/fhir")]
public class FhirController : ControllerBase
{
    private readonly IFhirTranslationService _translator;
    private readonly ILogger<FhirController> _logger;

    public FhirController(IFhirTranslationService translator, ILogger<FhirController> logger)
    {
        _translator = translator;
        _logger = logger;
    }

    /// <summary>
    /// Translate a raw HL7 v2 message (text/plain) into a FHIR R4 Bundle (application/fhir+json).
    /// Supported message types: ADT^A01, ORU^R01.
    /// </summary>
    [HttpPost("translate")]
    [Consumes(ContentTypes.PlainText)]
    [Produces(ContentTypes.FhirJson)]
    public async Task<IActionResult> Translate()
    {
        using var reader = new StreamReader(Request.Body);
        var rawMessage = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(rawMessage))
            return BadRequest(new { error = "Request body must contain an HL7 message" });

        try
        {
            var fhirJson = _translator.TranslateToJson(rawMessage);
            return Content(fhirJson, ContentTypes.FhirJson);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning("Unsupported HL7 message type: {Error}", ex.Message);
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FHIR translation failed");
            return UnprocessableEntity(new { error = $"Translation failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// JSON-wrapped translation endpoint — easier to call from Postman/Swagger
    /// than the raw text/plain endpoint.
    /// </summary>
    [HttpPost("translate/json")]
    [Consumes(ContentTypes.Json)]
    [Produces(ContentTypes.FhirJson)]
    public IActionResult TranslateFromJson([FromBody] FhirTranslateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message field is required" });

        try
        {
            var fhirJson = _translator.TranslateToJson(request.Message);
            return Content(fhirJson, ContentTypes.FhirJson);
        }
        catch (NotSupportedException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FHIR translation failed");
            return UnprocessableEntity(new { error = $"Translation failed: {ex.Message}" });
        }
    }
}

/// <summary>Request body for the JSON-wrapped FHIR translation endpoint.</summary>
public class FhirTranslateRequest
{
    public string Message { get; set; } = "";
}
