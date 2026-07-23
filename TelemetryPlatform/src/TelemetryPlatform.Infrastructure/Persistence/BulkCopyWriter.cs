using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelemetryPlatform.Domain.Entities;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;

namespace TelemetryPlatform.Infrastructure.Persistence;

/// <summary>
/// Dedicated background service for batching and persisting telemetry.
/// Separates database I/O latency from the high-frequency SignalR processing thread.
/// </summary>
public sealed class BulkCopyWriter : BackgroundService
{
    private readonly ITelemetryRepository _repository;
    private readonly ILogger<BulkCopyWriter> _logger;
    private readonly Channel<SensorReading> _channel;
    
    // Batch configuration
    private readonly int _maxBatchSize = 10000;
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(1);

    public BulkCopyWriter(ITelemetryRepository repository, ILogger<BulkCopyWriter> logger)
    {
        _repository = repository;
        _logger = logger;
        
        // Unbounded channel: we assume the DB can eventually keep up, 
        // or we bound it and drop/backpressure. Bounded to 100k points.
        _channel = Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Enqueues a point for batch persistence. Non-blocking.
    /// </summary>
    public void Enqueue(TelemetryPoint point)
    {
        var reading = new SensorReading
        {
            TimestampMs = point.Timestamp,
            SensorId = point.SensorId,
            Value = point.Value,
            Flags = point.Flags
        };
        
        _channel.Writer.TryWrite(reading);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BulkCopyWriter started.");
        
        var batch = new List<SensorReading>(_maxBatchSize);
        using var timer = new PeriodicTimer(_flushInterval);

        try
        {
            Task<bool>? readTask = null;
            Task<bool>? timerTask = null;

            while (!stoppingToken.IsCancellationRequested)
            {
                readTask ??= _channel.Reader.WaitToReadAsync(stoppingToken).AsTask();
                timerTask ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                
                await Task.WhenAny(readTask, timerTask);
                
                // Drain the channel up to max batch size
                while (batch.Count < _maxBatchSize && _channel.Reader.TryRead(out var reading))
                {
                    batch.Add(reading);
                }

                if (batch.Count > 0 && (batch.Count >= _maxBatchSize || timerTask.IsCompleted))
                {
                    await FlushBatchAsync(batch, stoppingToken);
                }

                if (readTask.IsCompleted)
                {
                    if (!await readTask) break; // Channel closed
                    readTask = null;
                }
                
                if (timerTask.IsCompleted)
                {
                    await timerTask; // Propagate any errors rather than silently swallowing
                    timerTask = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining on shutdown
            while (batch.Count < _maxBatchSize && _channel.Reader.TryRead(out var reading))
            {
                batch.Add(reading);
            }
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, CancellationToken.None);
            }
            
            _logger.LogInformation("BulkCopyWriter shutting down gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "BulkCopyWriter crashed unexpectedly.");
            throw;
        }
    }

    private async Task FlushBatchAsync(List<SensorReading> batch, CancellationToken ct)
    {
        try
        {
            await _repository.BulkInsertAsync(batch, ct);
            batch.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush telemetry batch of size {Size}.", batch.Count);
            // In a real app we might retry with backoff, or write to a dead-letter queue.
            // For now, clear to avoid out of memory.
            batch.Clear(); 
        }
    }
}
