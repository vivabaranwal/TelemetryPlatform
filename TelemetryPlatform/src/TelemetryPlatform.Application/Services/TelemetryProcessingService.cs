using TelemetryPlatform.Application.DTOs;
using TelemetryPlatform.Application.Interfaces;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace TelemetryPlatform.Application.Services;

/// <summary>
/// Orchestrates the per-point processing pipeline:
///   1. Run anomaly detection.
///   2. If an anomaly is found, broadcast an AlertFrame to clients.
///   3. Accumulate points for batch persistence (handled by the caller/worker).
///
/// This is a pure application-layer service: no Infrastructure types, no SignalR,
/// no Npgsql.  All I/O goes through injected interfaces.
///
/// NOTE: Phase 3 will expand this with the batch flush logic and richer telemetry.
/// </summary>
public sealed class TelemetryProcessingService
{
    private readonly IAnomalyDetector _anomalyDetector;
    private readonly IAlertBroadcaster _alertBroadcaster;
    private readonly ILogger<TelemetryProcessingService> _logger;

    public TelemetryProcessingService(
        IAnomalyDetector anomalyDetector,
        IAlertBroadcaster alertBroadcaster,
        ILogger<TelemetryProcessingService> logger)
    {
        _anomalyDetector = anomalyDetector;
        _alertBroadcaster = alertBroadcaster;
        _logger = logger;
    }

    /// <summary>
    /// Process a single telemetry point.
    /// Called in a tight loop from the background worker — must be as cheap as possible.
    ///
    /// Returns a non-null <see cref="AlertFrameDto"/> if an anomaly was detected
    /// (caller may use this for additional processing, e.g., database anomaly table).
    ///
    /// Note: parameter is passed by value (not `in`) because async methods cannot
    /// use `in` parameters in C# — TelemetryPoint is 16 bytes so copy cost is minimal.
    /// </summary>
    public ValueTask<AlertFrameDto?> ProcessAsync(
        TelemetryPoint point,
        CancellationToken cancellationToken)
    {
        var anomaly = _anomalyDetector.Analyse(in point);

        if (anomaly is null) return new ValueTask<AlertFrameDto?>((AlertFrameDto?)null);

        var alert = new AlertFrameDto
        {
            Timestamp = point.Timestamp,
            SensorId = point.SensorId,
            Value = point.Value,
            ZScore = anomaly.Value.ZScore,
            WindowMean = anomaly.Value.WindowMean,
            WindowStdDev = anomaly.Value.WindowStdDev,
            AbsoluteThreshold = 3500.0, // TODO: read from options in Phase 3
            Reason = anomaly.Value.Reason,
            Severity = anomaly.Value.Severity,
        };

        // Fire-and-forget with explicit try/catch — a broadcast failure must NOT
        // interrupt the processing loop.
        try
        {
            _ = _alertBroadcaster.BroadcastAlertAsync(alert, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Alert broadcast failed for Sensor={SensorId}. Pipeline continues.",
                point.SensorId);
        }

        return new ValueTask<AlertFrameDto?>(alert);
    }
}
