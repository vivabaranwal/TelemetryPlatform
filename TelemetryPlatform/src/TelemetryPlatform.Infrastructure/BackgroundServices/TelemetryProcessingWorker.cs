using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemetryPlatform.Application.DTOs;
using TelemetryPlatform.Application.Services;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;
using TelemetryPlatform.Infrastructure.Channels;
using TelemetryPlatform.Infrastructure.SignalR;

namespace TelemetryPlatform.Infrastructure.BackgroundServices;

/// <summary>Options for the background processing worker.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Only broadcast every Nth packet to connected dashboard clients.
    /// At 1 000 pps, BroadcastSampleRate=10 sends 100 packets/sec to the frontend.
    /// The chart's useRef buffer can absorb all 1000; the broadcast is just for the visible stream.
    /// </summary>
    public int BroadcastSampleRate { get; set; } = 10;

    /// <summary>How often to log channel depth and throughput statistics (in seconds).</summary>
    public int MetricsLogIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Long-running <see cref="BackgroundService"/> that continuously drains the telemetry channel.
///
/// Processing pipeline per point:
///   1. <c>await WaitToReadAsync</c> — yields until data is available (no busy-wait).
///   2. <c>TryRead</c> in a tight inner loop — drain as many points as possible per wake.
///   3. Run anomaly detection (Application layer — no I/O).
///   4. Broadcast sampled points and alerts via SignalR (non-blocking fire-and-forget).
///   5. Accumulate batch for DB flush (Phase 3: BulkCopyWriter).
///
/// Cancellation:
///   All async calls receive the <see cref="CancellationToken"/> from the host.
///   On shutdown, the token fires, WaitToReadAsync throws OperationCancelledException,
///   the outer try/catch catches it and logs "shutting down" — clean exit.
/// </summary>
public sealed class TelemetryProcessingWorker : BackgroundService
{
    private readonly ITelemetryChannel _channel;
    private readonly TelemetryProcessingService _processingService;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly TelemetryChannelSingleton _channelSingleton;
    private readonly WorkerOptions _options;
    private readonly ILogger<TelemetryProcessingWorker> _logger;
    private readonly Persistence.BulkCopyWriter _batchWriter;

    // Rolling metrics (not thread-shared — only touched by this background thread)
    private long _processedCount;
    private long _lastMetricsLog; // Environment.TickCount64 at last log

    public TelemetryProcessingWorker(
        ITelemetryChannel channel,
        TelemetryProcessingService processingService,
        IHubContext<TelemetryHub> hubContext,
        TelemetryChannelSingleton channelSingleton, // for drop metrics
        Persistence.BulkCopyWriter batchWriter,
        IOptions<WorkerOptions> options,
        ILogger<TelemetryProcessingWorker> logger)
    {
        _channel = channel;
        _processingService = processingService;
        _hubContext = hubContext;
        _channelSingleton = channelSingleton;
        _batchWriter = batchWriter;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TelemetryProcessingWorker started.");
        _lastMetricsLog = Environment.TickCount64;

        try
        {
            while (await _channel.WaitToReadAsync(stoppingToken))
            {
                // Inner tight loop — drain everything available without re-awaiting
                while (_channel.TryRead(out TelemetryPoint point))
                {
                    await ProcessPointAsync(point, stoppingToken);
                }

                MaybeLogMetrics();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("TelemetryProcessingWorker shutting down gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "TelemetryProcessingWorker crashed unexpectedly.");
            throw; // Let the host decide to restart
        }
        finally
        {
            _logger.LogInformation(
                "TelemetryProcessingWorker stopped. Total processed: {Count}", _processedCount);
        }
    }

    private async ValueTask ProcessPointAsync(TelemetryPoint point, CancellationToken ct)
    {
        _processedCount++;

        // 1. Run anomaly detection + alert broadcast (handled by processing service)
        AlertFrameDto? alert = await _processingService.ProcessAsync(point, ct);

        // 2. Broadcast sampled points to dashboard (randomized to prevent harmonic resonance with sequential ingestion)
        if (Random.Shared.Next(1, _options.BroadcastSampleRate + 1) == 1)
        {
            var dto = new TelemetryPacketDto
            {
                Timestamp = point.Timestamp,
                SensorId = point.SensorId,
                Value = point.Value,
                Flags = point.Flags,
            };

            // Fire-and-forget — do not await; use _ to suppress CS4014
            _ = _hubContext.Clients
                .Group(TelemetryHub.DashboardGroup)
                .SendAsync("ReceiveTelemetry", dto, ct);
        }

        // 3. Phase 3: Batch persist via BulkCopyWriter
        _batchWriter.Enqueue(point);
    }

    private void MaybeLogMetrics()
    {
        long now = Environment.TickCount64;
        long elapsed = now - _lastMetricsLog;

        if (elapsed < _options.MetricsLogIntervalSeconds * 1000L) return;

        _logger.LogInformation(
            "Telemetry metrics — Processed: {Processed} | ChannelDepth: {Depth} | Dropped: {Dropped}",
            _processedCount,
            _channel.ApproximateCount,
            _channelSingleton.TotalDropped);

        _lastMetricsLog = now;
    }
}
