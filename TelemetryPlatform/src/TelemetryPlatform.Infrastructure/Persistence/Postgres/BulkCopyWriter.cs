// Phase 3 placeholder — PostgreSQL COPY bulk writer.
// Full implementation added in Phase 3 when persistence backend is confirmed.
// See ITelemetryRepository in Domain for the contract this will implement.

namespace TelemetryPlatform.Infrastructure.Persistence.Postgres;

/// <summary>
/// PostgreSQL COPY-based bulk writer for telemetry data.
///
/// Phase 3 implementation will use Npgsql's binary COPY API:
///   await using var writer = await conn.BeginBinaryImportAsync(
///       "COPY sensor_readings (timestamp_ms, sensor_id, value, flags) FROM STDIN (FORMAT BINARY)");
///
/// Key design decisions for Phase 3:
///  - Buffer N points in a List<SensorReading>, flush every M seconds (configurable).
///  - Retry with exponential backoff on Npgsql exceptions.
///  - Use a separate NpgsqlConnection per flush (not shared — COPY monopolises the connection).
///  - TODO(Phase3): Implement ITelemetryRepository.
/// </summary>
public sealed class BulkCopyWriter
{
    // Phase 3 implementation pending persistence backend confirmation.
}
