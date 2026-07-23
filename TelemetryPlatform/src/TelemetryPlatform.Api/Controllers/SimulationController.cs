using Microsoft.AspNetCore.Mvc;
using TelemetryPlatform.Infrastructure.Simulation;

namespace TelemetryPlatform.Api.Controllers;

/// <summary>
/// HTTP control surface for the in-process sensor simulation.
///
/// Endpoints:
///   POST /api/simulation/start  — start the simulation loop
///   POST /api/simulation/stop   — stop the simulation loop (awaits graceful completion)
///   GET  /api/simulation/status — returns running state + metrics
///
/// CORS is handled globally by the "DevDashboard" policy in Program.cs.
/// Authentication is intentionally omitted (demo / local usage only).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class SimulationController : ControllerBase
{
    private readonly SimulationService _simulation;
    private readonly ILogger<SimulationController> _logger;

    public SimulationController(SimulationService simulation, ILogger<SimulationController> logger)
    {
        _simulation = simulation;
        _logger     = logger;
    }

    // ── GET /api/simulation/status ────────────────────────────────────────────

    /// <summary>
    /// Returns the current simulation state.
    /// Safe to call from the frontend on page load to sync button state after a
    /// browser refresh.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SimulationStatusResponse), StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var status = _simulation.GetStatus();
        return Ok(MapToResponse(status));
    }

    // ── POST /api/simulation/start ────────────────────────────────────────────

    /// <summary>
    /// Starts the in-process simulation.
    /// Returns 409 Conflict if the simulation is already running.
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(typeof(SimulationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult Start()
    {
        bool started = _simulation.Start();

        if (!started)
        {
            _logger.LogWarning("POST /api/simulation/start called while already running.");
            return Conflict(new ProblemDetails
            {
                Title  = "Simulation already running",
                Detail = "Call POST /api/simulation/stop first.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        return Ok(MapToResponse(_simulation.GetStatus()));
    }

    // ── POST /api/simulation/stop ─────────────────────────────────────────────

    /// <summary>
    /// Stops the simulation gracefully.  Waits for the current tick to complete.
    /// Returns 409 Conflict if the simulation is already stopped.
    /// </summary>
    [HttpPost("stop")]
    [ProducesResponseType(typeof(SimulationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Stop()
    {
        bool stopped = await _simulation.StopAsync();

        if (!stopped)
        {
            _logger.LogWarning("POST /api/simulation/stop called while already stopped.");
            return Conflict(new ProblemDetails
            {
                Title  = "Simulation is not running",
                Detail = "Call POST /api/simulation/start first.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        return Ok(MapToResponse(_simulation.GetStatus()));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static SimulationStatusResponse MapToResponse(SimulationStatus status) =>
        new(
            IsRunning:     status.IsRunning,
            StartedAt:     status.StartedAt,
            PacketsSent:   status.PacketsSent,
            SensorsActive: status.SensorsActive);
}

/// <summary>Response DTO for all simulation endpoints.</summary>
/// <param name="IsRunning">True when the simulation loop is active.</param>
/// <param name="StartedAt">UTC time the current run started, or null if stopped.</param>
/// <param name="PacketsSent">Total packets written to the channel in the current run.</param>
/// <param name="SensorsActive">Number of concurrent sensor tasks.</param>
public sealed record SimulationStatusResponse(
    bool            IsRunning,
    DateTimeOffset? StartedAt,
    long            PacketsSent,
    int             SensorsActive);
