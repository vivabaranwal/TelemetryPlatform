using Microsoft.AspNetCore.SignalR;
using TelemetryPlatform.Application.DTOs;
using TelemetryPlatform.Application.Interfaces;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace TelemetryPlatform.Infrastructure.SignalR;

/// <summary>
/// SignalR hub for real-time bidirectional telemetry communication.
///
/// INBOUND (simulator/hardware → server):
///   Clients call <see cref="SendTelemetry"/> which performs a synchronous
///   <see cref="ITelemetryChannel.TryWrite"/> and immediately returns.
///   Zero awaits. Zero blocking. The packet is either queued or silently dropped.
///
/// OUTBOUND (server → dashboard):
///   The <see cref="TelemetryProcessingWorker"/> calls <see cref="BroadcastTelemetryAsync"/>
///   to push processed points, and <see cref="BroadcastAlertAsync"/> (via IAlertBroadcaster)
///   to push anomaly alerts.
///
/// TODO (hardening pass): Add authentication/authorization attributes.
/// TODO (hardening pass): Add connection rate limiting to prevent DoS from rogue simulators.
/// </summary>
public sealed class TelemetryHub : Hub, IAlertBroadcaster
{
    private readonly ITelemetryChannel _channel;
    private readonly ILogger<TelemetryHub> _logger;

    // Hub group name for all connected dashboard clients
    public const string DashboardGroup = "dashboard";

    public TelemetryHub(ITelemetryChannel channel, ILogger<TelemetryHub> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    /// <summary>
    /// Called by the sensor simulator / hardware adapter to ingest a telemetry packet.
    ///
    /// CRITICAL: This method is synchronous — no async, no await.
    ///   TryWrite is a non-blocking lock-free operation.
    ///   If the channel is full, the packet is silently dropped (logged at Debug level).
    ///   The SignalR network thread returns immediately in all cases.
    /// </summary>
    public void SendTelemetry(TelemetryPacketDto packet)
    {
        var point = new TelemetryPoint(
            timestamp: packet.Timestamp,
            sensorId: packet.SensorId,
            value: packet.Value,
            flags: packet.Flags);

        bool written = _channel.TryWrite(point);

        if (!written)
        {
            _logger.LogDebug(
                "Channel full — dropped packet from Sensor={SensorId} at T={Timestamp}",
                packet.SensorId, packet.Timestamp);
        }
    }

    /// <summary>
    /// Push a processed telemetry point to all dashboard clients.
    /// Called by <see cref="TelemetryProcessingWorker"/> on the reader thread.
    ///
    /// Only called for points that pass a configurable sampling rate
    /// (e.g., every 10th point) to prevent WebSocket saturation at 1000 pps.
    /// </summary>
    public async Task BroadcastTelemetryAsync(
        TelemetryPacketDto packet,
        CancellationToken cancellationToken = default)
    {
        await Clients.Group(DashboardGroup)
            .SendAsync("ReceiveTelemetry", packet, cancellationToken);
    }

    /// <inheritdoc cref="IAlertBroadcaster.BroadcastAlertAsync"/>
    public async Task BroadcastAlertAsync(
        AlertFrameDto alert,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Broadcasting alert to dashboard: Sensor={SensorId} Severity={Severity} Reason={Reason}",
            alert.SensorId, alert.Severity, alert.Reason);

        await Clients.Group(DashboardGroup)
            .SendAsync("ReceiveAlert", alert, cancellationToken);
    }

    // ── Connection lifecycle ───────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        string connectionId = Context.ConnectionId;
        _logger.LogInformation("Client connected: {ConnectionId}", connectionId);

        // Auto-join all connections to the dashboard broadcast group.
        // Phase hardening: differentiate simulator connections from dashboard connections
        // using claims/query-string and only add dashboard clients to this group.
        await Groups.AddToGroupAsync(connectionId, DashboardGroup);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            _logger.LogWarning(exception,
                "Client disconnected with error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogInformation(
                "Client disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
