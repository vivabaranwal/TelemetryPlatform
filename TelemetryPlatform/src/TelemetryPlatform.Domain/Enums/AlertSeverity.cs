namespace TelemetryPlatform.Domain.Enums;

/// <summary>
/// Severity classification for anomaly detection alerts.
/// Ordered from least to most severe so numeric comparison works correctly
/// (e.g., severity >= AlertSeverity.Critical).
/// </summary>
public enum AlertSeverity : byte
{
    /// <summary>No anomaly detected.  Default/normal state.</summary>
    None = 0,

    /// <summary>
    /// Value is within normal operating range but trending toward a threshold.
    /// Used for predictive maintenance signals — no immediate operator action required.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Value has exceeded an operational threshold.  Requires operator attention.
    /// Example: hydraulic pressure above 3000 psi with rising Z-score.
    /// </summary>
    High = 2,

    /// <summary>
    /// Value has exceeded a safety-critical threshold AND Z-score confirms it is not noise.
    /// Example: pressure spike above 3500 psi sustained for 5+ consecutive frames.
    /// Triggers an immediate alert push to all connected clients.
    /// </summary>
    Critical = 3,
}
