// HealthBridge HL7/DICOM Service — ASP.NET Core 8 entry point
//
// This is the C# microservice that handles healthcare data parsing:
//   - HL7 v2 message parsing (ADT admissions, ORU lab results)
//   - DICOM medical imaging metadata extraction
//
// All dependencies are wired here via the built-in DI container.
// The service runs on port 5001 and is accessed through the Go API gateway.

using HealthBridge.HL7Service.Services;
using HealthBridge.HL7Service.Strategies;
using HealthBridge.HL7Service.Builders;
using HealthBridge.HL7Service.Security;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- ASP.NET Core framework services ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "HealthBridge HL7/DICOM Service",
        Version = "v1",
        Description = "Parses and validates HL7 v2 messages and DICOM metadata"
    });
});

// --- Application dependency injection ---

// ACK builder — generates HL7 acknowledgement messages
builder.Services.AddSingleton<IAckBuilder, Hl7AckBuilder>();

// Strategy Pattern — each HL7 message type gets its own extractor.
// Registered as IEnumerable<IMessageExtractorStrategy> so the parser
// iterates through them via CanHandle() to find the right one.
// Order matters: DefaultExtractorStrategy must be last (it's the catch-all).
builder.Services.AddSingleton<IMessageExtractorStrategy, AdtExtractorStrategy>();
builder.Services.AddSingleton<IMessageExtractorStrategy, OruExtractorStrategy>();
builder.Services.AddSingleton<IMessageExtractorStrategy, DefaultExtractorStrategy>();

// FHIR translation strategies — same Strategy Pattern, different output (FHIR R4 resources).
// Each strategy converts one HL7 v2 message type to a FHIR Bundle.
builder.Services.AddSingleton<IFhirTranslatorStrategy, AdtToFhirStrategy>();
builder.Services.AddSingleton<IFhirTranslatorStrategy, OruToFhirStrategy>();

// --- HIPAA Security: PHI encryption + audit logging ---
// IHttpContextAccessor lets the audit decorator capture the user/IP/request ID
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IEncryptionService, AesEncryptionService>();
builder.Services.AddSingleton<IPhiAuditService, FilePhiAuditService>();

// Core services — HL7 parsing (nHapi), DICOM extraction (fo-dicom), FHIR translation
//
// Decorator Pattern: register the concrete HL7ParserService as itself,
// then expose IHL7ParserService through the auditing decorator.
// Callers see one interface; the decorator transparently logs every PHI access.
builder.Services.AddSingleton<HL7ParserService>();
builder.Services.AddSingleton<IHL7ParserService>(sp => new AuditingHL7ParserDecorator(
    sp.GetRequiredService<HL7ParserService>(),
    sp.GetRequiredService<IPhiAuditService>(),
    sp.GetRequiredService<IHttpContextAccessor>()));

builder.Services.AddSingleton<IDicomService, DicomService>();

// Same Decorator Pattern for FHIR translation — audit every translation as PHI access
builder.Services.AddSingleton<FhirTranslationService>();
builder.Services.AddSingleton<IFhirTranslationService>(sp => new AuditingFhirTranslationDecorator(
    sp.GetRequiredService<FhirTranslationService>(),
    sp.GetRequiredService<IPhiAuditService>(),
    sp.GetRequiredService<IHttpContextAccessor>()));

// --- OpenTelemetry distributed tracing ---
// Auto-instruments incoming HTTP requests and outgoing HttpClient calls.
// Traces export via OTLP to a collector (Jaeger, Tempo, AWS X-Ray, etc.)
// Endpoint configurable via OTEL_EXPORTER_OTLP_ENDPOINT env var.
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
                   ?? "http://jaeger:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: "hl7-service", serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("HealthBridge.HL7Service")  // Custom spans from our code
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = new Uri(otlpEndpoint);
        }));

var app = builder.Build();

// Swagger UI available at /swagger for interactive API testing
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

// Redirect root URL to Swagger for convenience
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
