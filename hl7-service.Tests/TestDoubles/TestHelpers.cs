using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HealthBridge.HL7Service.Tests.TestDoubles;

/// <summary>
/// Small builders for the framework types the production classes take as constructor
/// arguments. Keeping them here means each test reads as arrange/act/assert rather
/// than ten lines of ASP.NET plumbing.
/// </summary>
public static class TestHelpers
{
    /// <summary>A logger that discards output. Swap for a real one when debugging a test.</summary>
    public static ILogger<T> NullLoggerFor<T>() => NullLogger<T>.Instance;

    /// <summary>Builds an IConfiguration from a plain dictionary.</summary>
    public static IConfiguration ConfigWith(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>Empty configuration — used to exercise the "no key configured" fallbacks.</summary>
    public static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    /// <summary>
    /// An IHttpContextAccessor carrying a request with a known IP, user and trace ID,
    /// so audit-event assertions have concrete values to check.
    /// </summary>
    public static IHttpContextAccessor HttpContextWith(
        string? userName = null,
        string ip = "203.0.113.7",
        string traceId = "trace-abc-123")
    {
        var ctx = new DefaultHttpContext
        {
            TraceIdentifier = traceId
        };
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);

        if (userName != null)
        {
            ctx.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.Name, userName) },
                    authenticationType: "TestAuth"));
        }

        return new HttpContextAccessor { HttpContext = ctx };
    }

    /// <summary>An accessor with no current request — the background/non-HTTP path.</summary>
    public static IHttpContextAccessor NoHttpContext() => new HttpContextAccessor { HttpContext = null };

    /// <summary>
    /// Wires a controller up with a request whose body is the given text, for the
    /// endpoints that read Request.Body directly instead of taking a bound parameter.
    /// </summary>
    public static void GiveRequestBody(ControllerBase controller, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentType = "text/plain";
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }
}
