using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelemetryPlatform.Domain.Entities;
using TelemetryPlatform.Domain.Interfaces;

namespace TelemetryPlatform.Infrastructure.Persistence.Postgres;

public sealed class PostgresTelemetryRepository : ITelemetryRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresTelemetryRepository> _logger;

    public PostgresTelemetryRepository(NpgsqlDataSource dataSource, ILogger<PostgresTelemetryRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<int> BulkInsertAsync(IReadOnlyList<SensorReading> readings, CancellationToken cancellationToken)
    {
        if (readings.Count == 0) return 0;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            
            // Fast binary COPY
            using var writer = await connection.BeginBinaryImportAsync(
                "COPY sensor_readings (timestamp_ms, sensor_id, value, flags) FROM STDIN (FORMAT BINARY)",
                cancellationToken);

            foreach (var r in readings)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(r.TimestampMs, NpgsqlTypes.NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(r.SensorId, NpgsqlTypes.NpgsqlDbType.Smallint, cancellationToken);
                await writer.WriteAsync(r.Value, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(r.Flags, NpgsqlTypes.NpgsqlDbType.Smallint, cancellationToken);
            }

            // Complete the copy operation
            ulong rowsWritten = await writer.CompleteAsync(cancellationToken);
            return (int)rowsWritten;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to bulk insert {Count} telemetry records into Postgres.", readings.Count);
            throw new TelemetryPersistenceException("Binary COPY failed", ex);
        }
    }

    public async Task<long> GetTotalCountAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT reltuples::bigint AS estimate FROM pg_class WHERE relname = 'sensor_readings';";
            
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is long count ? count : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get total count estimate.");
            return -1;
        }
    }
}
