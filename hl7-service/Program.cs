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
using Serilog;
using Serilog.Events;
using Serilog.Templates;

var builder = WebApplication.CreateBuilder(args);

// One source of truth for the service name — used by both the Serilog enricher
// below and the OpenTelemetry resource further down, so logs and traces always
// agree. docker-compose.yml sets OTEL_SERVICE_NAME; the literal is the fallback.
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "hl7-service";

// --- Structured JSON logging  ---
// AddSerilog registers a SerilogLoggerFactory, which *replaces* ILoggerFactory
// rather than joining the provider list. The built-in console/debug providers
// are therefore bypassed: no double output, no ClearProviders() call needed.
//
// No call site changed. The existing logs already use message templates, so
// ..rest() promotes their named values ({Type}, {Id}, {Strategy} from
// Services/HL7ParserService.cs) to top-level JSON fields.
//
// ExpressionTemplate rather than CompactJsonFormatter so the field names are
// ours: CLEF's @t/@l/@mt clash with the @-prefixed fields CloudWatch Logs
// Insights reserves, and gateway (Go) and monitoring-service (Python) have to
// emit the same names for one query to span all three services.
builder.Services.AddSerilog((services, cfg) => cfg
    .MinimumLevel.Information()
    // Silences ASP.NET's three-lines-per-request chatter. It does not reach
    // UseSerilogRequestLogging below, which logs under a different category.
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    // Prerequisite for A2: middleware pushes request_id, every line picks it up.
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", serviceName)
    // The level names are spelled out by hand because Serilog.Expressions 5.0.0
    // ships ToLower but no ToUpper — and an unknown function resolves to
    // *undefined* rather than throwing, so the key would vanish from the output
    // with no error. Values match Go slog's defaults (INFO / WARN / ERROR).
    .WriteTo.Console(new ExpressionTemplate(
        "{ {" +
        "timestamp: UtcDateTime(@t), "+
        "level: if @l = 'Information' then 'INFO'" +
        " else if @l = 'Warning' then 'WARN'" +
        " else if @l = 'Error' then 'ERROR'" +
        " else if @l = 'Fatal' then 'FATAL'" +
        " else if @l = 'Debug' then 'DEBUG'" + 
        " else 'TRACE', " +
        // logger: SourceContext renames Serilog's category property to the name
        // Python and Go use. Naming it here also drops it from ..rest(), so it
        // is not emitted twice.
        "message: @m, logger: SourceContext, exception: @x, ..rest()} }\n"))
    // Last in the chain on purpose: with no "Serilog" section present this is a
    // no-op, but it lets Serilog__MinimumLevel__Default (an env var, so a
    // ConfigMap edit) raise verbosity on a running pod without an image rebuild.
    .ReadFrom.Configuration(builder.Configuration));

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
        .AddService(serviceName, serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("HealthBridge.HL7Service")  // Custom spans from our code
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = new Uri(otlpEndpoint);
        }));

var app = builder.Build();

// One log line per HTTP request (method, path, status, elapsed ms) — the access
// log. Placed before everything else so it wraps the whole pipeline. It logs
// under Serilog.AspNetCore.RequestLoggingMiddleware, so the Microsoft.AspNetCore
// level override above does not suppress it.
app.UseSerilogRequestLogging();

// Swagger UI available at /swagger for interactive API testing
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

// Redirect root URL to Swagger for convenience
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
