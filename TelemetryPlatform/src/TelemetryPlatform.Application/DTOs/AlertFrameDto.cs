using TelemetryPlatform.Domain.Enums;

namespace TelemetryPlatform.Application.DTOs;

/// <summary>
/// Outbound DTO pushed to clients via SignalR when the anomaly engine fires an alert.
///
/// Intentionally richer than TelemetryPoint — includes diagnostic fields (ZScore, Mean,
/// StdDev) so the dashboard can render useful context without a follow-up API call.
///
/// This type is kept as a class so SignalR's default MessagePack/JSON serializer works
/// without manual configuration.
/// </summary>
public sealed class AlertFrameDto
{
    /// <summary>Unix epoch milliseconds when the anomaly was detected.</summary>
    public long Timestamp { get; init; }

    /// <summary>Sensor that triggered the alert.</summary>
    public short SensorId { get; init; }

    /// <summary>The raw value that exceeded thresholds.</summary>
    public double Value { get; init; }

    /// <summary>Z-score of the triggering value within the rolling window.</summary>
    public double ZScore { get; init; }

    /// <summary>Rolling mean at the time of detection.</summary>
    public double WindowMean { get; init; }

    /// <summary>Rolling standard deviation at the time of detection.</summary>
    public double WindowStdDev { get; init; }

    /// <summary>Absolute threshold that was exceeded (e.g., 3500 psi).</summary>
    public double AbsoluteThreshold { get; init; }

    /// <summary>Human-readable reason string (e.g., "psi > 3500 AND z-score 4.2 over 5 frames").</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Severity classification for client-side alert banner rendering.</summary>
    public AlertSeverity Severity { get; init; }

    /// <summary>Unique alert identifier for deduplication on the client side.</summary>
    public Guid AlertId { get; init; } = Guid.NewGuid();
}
