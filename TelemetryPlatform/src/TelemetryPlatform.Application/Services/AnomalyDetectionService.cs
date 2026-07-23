using TelemetryPlatform.Application.DTOs;
using TelemetryPlatform.Application.Interfaces;
using TelemetryPlatform.Domain.Enums;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;
using TelemetryPlatform.Application.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TelemetryPlatform.Application.Services;

/// <summary>Configuration options for the anomaly detection engine.</summary>
public sealed class AnomalyDetectionOptions
{
    public const string SectionName = "AnomalyDetection";

    /// <summary>Number of recent readings retained per sensor for rolling statistics.</summary>
    public int RingBufferCapacity { get; set; } = 100;

    /// <summary>
    /// Minimum number of samples in the window before Z-score is computed.
    /// Prevents false positives on startup when the buffer is sparse.
    /// </summary>
    public int MinSamplesForStats { get; set; } = 10;

    /// <summary>Absolute value threshold. If exceeded, triggers a High alert candidate.</summary>
    public double AbsoluteThreshold { get; set; } = 3500.0;

    /// <summary>
    /// Z-score threshold.  A reading must exceed this to become a Critical alert.
    /// Combined with AbsoluteThreshold — both must be true for Critical severity.
    /// </summary>
    public double ZScoreThreshold { get; set; } = 3.0;

    /// <summary>Warning-level Z-score (lower threshold, generates Warning alert only).</summary>
    public double ZScoreWarningThreshold { get; set; } = 2.0;
}

/// <summary>
/// Stateful, per-instance anomaly detection engine.
///
/// Maintains one <see cref="RingBuffer"/> per sensor ID, computing rolling mean and
/// standard deviation using Welford's online algorithm (no LINQ, no intermediate lists).
///
/// Thread safety: This service is called from a single background processing thread
/// (TelemetryProcessingWorker).  The ConcurrentDictionary handles the unlikely race
/// where a new sensor is seen for the first time — all subsequent accesses are
/// single-threaded via the worker loop.
///
/// Implements <see cref="IAnomalyDetector"/> for testability.
/// </summary>
public sealed class AnomalyDetectionService : IAnomalyDetector
{
    private readonly AnomalyDetectionOptions _options;
    private readonly ILogger<AnomalyDetectionService> _logger;

    // ConcurrentDictionary for safe lazy initialisation of new sensors only.
    // After init, all access is from the single worker thread — no contention.
    private readonly ConcurrentDictionary<short, RingBuffer> _buffers = new();

    public AnomalyDetectionService(
        IOptions<AnomalyDetectionOptions> options,
        ILogger<AnomalyDetectionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public AnomalyResult? Analyse(in TelemetryPoint point)
    {
        // GetOrAdd is safe here: factory only allocates on first sight of a sensor.
        var buffer = _buffers.GetOrAdd(
            point.SensorId,
            _ => new RingBuffer(_options.RingBufferCapacity));

        buffer.Write(in point);

        // Not enough samples yet — skip stats to avoid false positives
        if (buffer.Count < _options.MinSamplesForStats)
            return null;

        if (!buffer.TryComputeStats(out double mean, out double stdDev))
            return null;

        double zScore = stdDev > 0.0
            ? Math.Abs(point.Value - mean) / stdDev
            : 0.0;

        bool exceedsAbsolute = point.Value > _options.AbsoluteThreshold;
        bool exceedsCriticalZ = zScore >= _options.ZScoreThreshold;
        bool exceedsWarningZ = zScore >= _options.ZScoreWarningThreshold;

        AlertSeverity severity;
        string reason;

        if (exceedsAbsolute && exceedsCriticalZ)
        {
            severity = AlertSeverity.Critical;
            reason = $"Value {point.Value:F2} > absolute threshold {_options.AbsoluteThreshold:F2} " +
                     $"AND Z-score {zScore:F2} >= {_options.ZScoreThreshold:F2} (mean={mean:F2}, σ={stdDev:F2})";
        }
        else if (exceedsAbsolute)
        {
            severity = AlertSeverity.High;
            reason = $"Value {point.Value:F2} > absolute threshold {_options.AbsoluteThreshold:F2} " +
                     $"(Z-score {zScore:F2} below critical threshold)";
        }
        else if (exceedsCriticalZ)
        {
            severity = AlertSeverity.High;
            reason = $"Z-score {zScore:F2} >= {_options.ZScoreThreshold:F2} " +
                     $"(value={point.Value:F2}, mean={mean:F2}, σ={stdDev:F2})";
        }
        else if (exceedsWarningZ)
        {
            severity = AlertSeverity.Warning;
            reason = $"Z-score {zScore:F2} >= warning threshold {_options.ZScoreWarningThreshold:F2}";
        }
        else
        {
            return null; // Common path — no anomaly
        }

        _logger.LogWarning(
            "ANOMALY [{Severity}] Sensor={SensorId} Value={Value:F2} ZScore={ZScore:F2} Reason={Reason}",
            severity, point.SensorId, point.Value, zScore, reason);

        return new AnomalyResult(point, zScore, mean, stdDev, reason, severity);
    }

    /// <summary>Returns the count of sensors currently tracked by the detection engine.</summary>
    public int TrackedSensorCount => _buffers.Count;
}
