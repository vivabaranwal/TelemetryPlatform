using TelemetryPlatform.Application.DTOs;

namespace TelemetryPlatform.Application.Interfaces;

/// <summary>
/// Abstraction for pushing <see cref="AlertFrameDto"/> objects to connected clients in real-time.
///
/// Kept in Application layer so <see cref="Services.AnomalyDetectionService"/> can call it
/// without knowing about SignalR (which lives in Infrastructure).
///
/// The Infrastructure.SignalR.TelemetryHub implements this interface and is injected via DI.
/// </summary>
public interface IAlertBroadcaster
{
    /// <summary>
    /// Broadcast an alert frame to all currently connected dashboard clients.
    ///
    /// Fire-and-forget: implementations should not block the caller.
    /// Any exceptions must be caught internally and logged — the processing loop
    /// cannot be interrupted by a failed broadcast.
    /// </summary>
    Task BroadcastAlertAsync(AlertFrameDto alert, CancellationToken cancellationToken = default);
}
