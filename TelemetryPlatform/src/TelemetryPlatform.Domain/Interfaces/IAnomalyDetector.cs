using TelemetryPlatform.Domain.ValueObjects;

namespace TelemetryPlatform.Domain.Interfaces;

/// <summary>
/// Contract for anomaly detection implementations.
/// Concrete implementations live in Application layer; this interface in Domain
/// allows the processing service to depend on the abstraction without coupling to
/// specific algorithms (Z-score today, ML tomorrow).
/// </summary>
public interface IAnomalyDetector
{
    /// <summary>
    /// Analyse a single telemetry point in context of the sensor's recent history.
    ///
    /// Implementations must:
    ///  - Maintain per-sensor state internally (e.g., ring buffer of recent values).
    ///  - Execute in O(window-size) time with zero heap allocations in the hot path.
    ///  - Be thread-safe if the same instance is used across multiple sensors concurrently.
    ///
    /// Returns <c>null</c> if no anomaly is detected (the common case).
    /// </summary>
    AnomalyResult? Analyse(in TelemetryPoint point);
}

/// <summary>
/// Lightweight result returned when an anomaly is detected.
/// Struct to avoid heap allocation on the hot path for the <c>null</c> (no-anomaly) case.
/// </summary>
public readonly struct AnomalyResult
{
    /// <summary>The point that triggered the anomaly.</summary>
    public readonly TelemetryPoint TriggerPoint;

    /// <summary>Z-score of the triggering value within the rolling window.</summary>
    public readonly double ZScore;

    /// <summary>Rolling mean at the time of the anomaly.</summary>
    public readonly double WindowMean;

    /// <summary>Rolling standard deviation at the time of the anomaly.</summary>
    public readonly double WindowStdDev;

    /// <summary>Human-readable description of why the anomaly was triggered.</summary>
    public readonly string Reason;

    /// <summary>Severity classification.</summary>
    public readonly Enums.AlertSeverity Severity;

    /// <summary>Initialises all fields of an anomaly result.</summary>
    public AnomalyResult(
        TelemetryPoint triggerPoint,
        double zScore,
        double windowMean,
        double windowStdDev,
        string reason,
        Enums.AlertSeverity severity)
    {
        TriggerPoint = triggerPoint;
        ZScore = zScore;
        WindowMean = windowMean;
        WindowStdDev = windowStdDev;
        Reason = reason;
        Severity = severity;
    }
}
