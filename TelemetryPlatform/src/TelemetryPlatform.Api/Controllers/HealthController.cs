using Microsoft.AspNetCore.Mvc;
using TelemetryPlatform.Infrastructure.Channels;

namespace TelemetryPlatform.Api.Controllers;

/// <summary>
/// Lightweight health and diagnostics endpoint.
/// Used by load balancers, Docker health checks, and the README's verification step.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class HealthController : ControllerBase
{
    private readonly TelemetryChannelSingleton _channel;

    public HealthController(TelemetryChannelSingleton channel)
    {
        _channel = channel;
    }

    /// <summary>
    /// Returns 200 OK with basic service info and channel metrics.
    /// No auth required — this endpoint is explicitly public.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var response = new HealthResponse
        {
            Status = "Healthy",
            Version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            UtcNow = DateTimeOffset.UtcNow,
            ChannelDepth = _channel.ApproximateCount,
            TotalPacketsReceived = _channel.TotalWritten,
            TotalPacketsDropped = _channel.TotalDropped,
        };

        return Ok(response);
    }
}

/// <summary>Response body for the health endpoint.</summary>
public sealed class HealthResponse
{
    public string Status { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset UtcNow { get; init; }
    public int ChannelDepth { get; init; }
    public long TotalPacketsReceived { get; init; }
    public long TotalPacketsDropped { get; init; }
}
