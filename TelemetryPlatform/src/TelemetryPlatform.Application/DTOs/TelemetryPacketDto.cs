namespace TelemetryPlatform.Application.DTOs;

/// <summary>
/// Wire DTO for a telemetry packet received from the sensor simulator or physical hardware.
/// Used at the API boundary (SignalR hub method parameter) before conversion to the
/// hot-path <see cref="TelemetryPlatform.Domain.ValueObjects.TelemetryPoint"/> struct.
///
/// Kept as a class (not struct) to allow JSON deserialization without custom converters.
/// Only lives at the ingestion boundary — never stored or queued in large volumes.
/// </summary>
public sealed class TelemetryPacketDto
{
    /// <summary>Unix epoch milliseconds.  Matches the TelemetryPoint.Timestamp field.</summary>
    public long Timestamp { get; init; }

    /// <summary>Numeric sensor identifier (matches TelemetryPoint.SensorId).</summary>
    public short SensorId { get; init; }

    /// <summary>Sensor reading in its native engineering unit.</summary>
    public double Value { get; init; }

    /// <summary>Optional flag bits.  0 = normal; bit 0 = simulator spike.</summary>
    public short Flags { get; init; }

    /// <summary>Human-readable sensor label sent by the simulator for debugging.</summary>
    public string? Label { get; init; }
}
