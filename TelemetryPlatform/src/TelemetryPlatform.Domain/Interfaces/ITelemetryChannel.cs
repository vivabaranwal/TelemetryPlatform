using TelemetryPlatform.Domain.ValueObjects;

namespace TelemetryPlatform.Domain.Interfaces;

/// <summary>
/// Abstraction over the high-throughput <see cref="System.Threading.Channels.Channel{T}"/>
/// singleton that decouples the SignalR hub (writer side) from the background worker (reader side).
///
/// Concrete implementation lives in Infrastructure.Channels — Domain never sees it directly,
/// preserving the zero-dependency rule.
/// </summary>
public interface ITelemetryChannel
{
    /// <summary>
    /// Attempt to write a point to the channel without blocking.
    /// Returns <c>false</c> if the channel is full (back-pressure signal).
    /// Must never throw; the caller on the network thread cannot handle exceptions here.
    /// </summary>
    bool TryWrite(TelemetryPoint point);

    /// <summary>
    /// Asynchronously wait until the channel has data available for reading.
    /// Used by the background processing worker to drain points without busy-waiting.
    /// </summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-blocking attempt to read the next available point.
    /// Should only be called after <see cref="WaitToReadAsync"/> returns <c>true</c>.
    /// </summary>
    bool TryRead(out TelemetryPoint point);

    /// <summary>
    /// Approximate number of unconsumed items currently waiting in the channel.
    /// Used for metrics/logging — not guaranteed to be exact.
    /// </summary>
    int ApproximateCount { get; }
}
