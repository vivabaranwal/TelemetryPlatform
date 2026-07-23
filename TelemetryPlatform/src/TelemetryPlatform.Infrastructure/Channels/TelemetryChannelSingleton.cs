using System.Threading.Channels;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace TelemetryPlatform.Infrastructure.Channels;

/// <summary>Configuration for the telemetry channel.</summary>
public sealed class TelemetryChannelOptions
{
    public const string SectionName = "TelemetryChannel";

    /// <summary>
    /// Maximum number of unprocessed TelemetryPoints the channel will buffer.
    ///
    /// Sizing guidance:
    ///   At 1 000 packets/sec and a target 5s processing latency budget,
    ///   a capacity of 8 192 provides ~8s headroom before overflow.
    ///   Keep as a power of 2 for allocator alignment.
    /// </summary>
    public int Capacity { get; set; } = 8192;

    /// <summary>
    /// Behaviour when the channel is full.
    ///
    /// TRADEOFFS:
    ///   DropOldest (default): Discards oldest buffered points when full.
    ///     PRO: SignalR network thread never blocks.
    ///     PRO: Always preserves freshest sensor data (most actionable for real-time monitoring).
    ///     CON: Old data is silently lost during sustained overload.
    ///
    ///   Wait: Blocks/back-pressures the writer until space is available.
    ///     PRO: Zero data loss.
    ///     CON: Blocks the SignalR network thread — catastrophic at high concurrency.
    ///
    ///   DropWrite: Discards the INCOMING point when full.
    ///     PRO: Never blocks.
    ///     CON: Drops the freshest data — the opposite of what we want for streaming dashboards.
    ///
    /// DECISION: DropOldest — in real-time telemetry, fresh data beats historical data.
    /// Dropped-packet counters are logged for observability.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.DropOldest;
}

/// <summary>
/// Singleton wrapper around <see cref="Channel{TelemetryPoint}"/>.
///
/// Single instance shared between:
///   - TelemetryHub (writer): calls TryWrite on every incoming packet.
///   - TelemetryProcessingWorker (reader): drains via WaitToReadAsync/TryRead.
///
/// Implements <see cref="ITelemetryChannel"/> so the Domain and Application layers
/// can depend on the abstraction without referencing System.Threading.Channels directly.
/// </summary>
public sealed class TelemetryChannelSingleton : ITelemetryChannel
{
    private readonly Channel<TelemetryPoint> _channel;

    // Metrics (thread-safe via Interlocked)
    private long _totalWritten;
    private long _totalDropped;

    public TelemetryChannelSingleton(IOptions<TelemetryChannelOptions> options)
    {
        var opts = options.Value;

        _channel = Channel.CreateBounded<TelemetryPoint>(
            new BoundedChannelOptions(opts.Capacity)
            {
                FullMode = opts.FullMode,
                // Single reader (worker), multiple writers (hub connections)
                SingleReader = true,
                SingleWriter = false,
                // Allow synchronous continuations for max throughput on reader side
                AllowSynchronousContinuations = true,
            });
    }

    /// <inheritdoc/>
    public bool TryWrite(TelemetryPoint point)
    {
        bool written = _channel.Writer.TryWrite(point);
        if (written)
            Interlocked.Increment(ref _totalWritten);
        else
            Interlocked.Increment(ref _totalDropped);
        return written;
    }

    /// <inheritdoc/>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    /// <inheritdoc/>
    public bool TryRead(out TelemetryPoint point) =>
        _channel.Reader.TryRead(out point);

    /// <inheritdoc/>
    public int ApproximateCount => _channel.Reader.Count;

    /// <summary>Total packets successfully written since startup (for metrics/logging).</summary>
    public long TotalWritten => Interlocked.Read(ref _totalWritten);

    /// <summary>Total packets dropped due to channel-full condition since startup.</summary>
    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    /// <summary>Signal channel completion on graceful shutdown.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
