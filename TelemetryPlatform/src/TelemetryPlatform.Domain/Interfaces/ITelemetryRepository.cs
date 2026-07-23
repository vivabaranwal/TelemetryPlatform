using TelemetryPlatform.Domain.Entities;

namespace TelemetryPlatform.Domain.Interfaces;

/// <summary>
/// Abstraction over the persistence backend for bulk telemetry writes.
///
/// Implementations (Postgres COPY, Redis TimeSeries) live in Infrastructure.
/// Application-layer services depend only on this interface, keeping them
/// infrastructure-agnostic and unit-testable with a mock.
/// </summary>
public interface ITelemetryRepository
{
    /// <summary>
    /// Persist a batch of sensor readings atomically.
    ///
    /// Implementations must:
    ///  - Use the most efficient bulk-write mechanism available (e.g., Npgsql COPY).
    ///  - Be cancellable — if the cancellation token fires, abort gracefully.
    ///  - Throw <see cref="TelemetryPersistenceException"/> on unrecoverable errors
    ///    so the caller can apply backoff and retry.
    /// </summary>
    /// <param name="readings">Batch of readings to persist.</param>
    /// <param name="cancellationToken">Propagated from the hosting BackgroundService.</param>
    /// <returns>Number of rows successfully written.</returns>
    Task<int> BulkInsertAsync(IReadOnlyList<SensorReading> readings, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the total count of persisted readings (used for health/diagnostic endpoints).
    /// </summary>
    Task<long> GetTotalCountAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by <see cref="ITelemetryRepository"/> implementations on non-transient write failures.
/// Callers should apply exponential backoff and retry a limited number of times.
/// </summary>
public sealed class TelemetryPersistenceException : Exception
{
    /// <summary>Initialises the exception with a message and optional inner exception.</summary>
    public TelemetryPersistenceException(string message, Exception? inner = null)
        : base(message, inner) { }
}
