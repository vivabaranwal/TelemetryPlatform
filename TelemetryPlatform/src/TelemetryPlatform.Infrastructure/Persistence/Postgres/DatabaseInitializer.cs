using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TelemetryPlatform.Infrastructure.Persistence.Postgres;

/// <summary>
/// Ensures the PostgreSQL schema exists on startup.
/// In production, use EF Core Migrations or a tool like DbUp / Flyway.
/// </summary>
public class DatabaseInitializer : IHostedService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing PostgreSQL database schema...");

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sensor_readings (
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    timestamp_ms BIGINT NOT NULL,
                    sensor_id SMALLINT NOT NULL,
                    value DOUBLE PRECISION NOT NULL,
                    flags SMALLINT NOT NULL
                );

                -- Index for time-series queries
                CREATE INDEX IF NOT EXISTS ix_sensor_readings_timestamp ON sensor_readings (timestamp_ms DESC);
                CREATE INDEX IF NOT EXISTS ix_sensor_readings_sensor_time ON sensor_readings (sensor_id, timestamp_ms DESC);
            ";

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Database schema initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database schema.");
            // We don't throw here to allow the app to start even if DB is temporarily down,
            // though depending on requirements we might want to crash-loop.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
