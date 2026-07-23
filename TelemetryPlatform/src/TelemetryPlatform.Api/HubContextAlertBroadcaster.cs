using Microsoft.AspNetCore.SignalR;
using TelemetryPlatform.Application.DTOs;
using TelemetryPlatform.Application.Interfaces;
using TelemetryPlatform.Infrastructure.SignalR;

namespace TelemetryPlatform.Api;

/// <summary>
/// Thin adapter that implements <see cref="IAlertBroadcaster"/> using
/// <see cref="IHubContext{TelemetryHub}"/>, allowing the Application-layer
/// <see cref="TelemetryPlatform.Application.Services.TelemetryProcessingService"/>
/// to broadcast alerts without directly referencing Infrastructure.SignalR.
///
/// Registered as Singleton in Program.cs — IHubContext is thread-safe.
/// </summary>
internal sealed class HubContextAlertBroadcaster : IAlertBroadcaster
{
    private readonly IHubContext<TelemetryHub> _hubContext;

    public HubContextAlertBroadcaster(IHubContext<TelemetryHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastAlertAsync(AlertFrameDto alert, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients
            .Group(TelemetryHub.DashboardGroup)
            .SendAsync("ReceiveAlert", alert, cancellationToken);
    }
}
