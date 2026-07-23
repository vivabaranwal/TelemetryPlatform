using System.Runtime.CompilerServices;
using TelemetryPlatform.Domain.ValueObjects;

namespace TelemetryPlatform.Application.Buffers;

/// <summary>
/// Allocation-light circular ring buffer for a single sensor's recent readings.
///
/// Design goals (enforced throughout):
///  - Fixed-capacity array allocated once at construction — no GC pressure in hot path.
///  - No LINQ, no boxing, no delegate allocations inside <see cref="Write"/> or statistics paths.
///  - All reads/writes via simple index arithmetic — O(1) write, O(N) statistics.
///  - NOT thread-safe: each sensor owns exactly one RingBuffer, accessed by the single
///    background processing thread.  No locking overhead required.
///
/// Memory layout:
///   _buffer:  TelemetryPoint[Capacity]  — inline array, allocated once.
///   _head:    int                        — index of next write position (wraps mod Capacity).
///   _count:   int                        — number of valid entries (≤ Capacity).
/// </summary>
public sealed class RingBuffer
{
    private readonly TelemetryPoint[] _buffer;
    private int _head;  // next write slot (circular)
    private int _count; // how many valid entries (saturates at Capacity)

    /// <summary>Maximum number of entries this buffer can hold.</summary>
    public int Capacity { get; }

    /// <summary>Number of entries currently held (0 ≤ Count ≤ Capacity).</summary>
    public int Count => _count;

    /// <summary>True when the buffer has been fully populated at least once.</summary>
    public bool IsFull => _count == Capacity;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        Capacity = capacity;
        _buffer = new TelemetryPoint[capacity];
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Write a new point into the buffer, overwriting the oldest entry when full.
    /// O(1), zero allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(in TelemetryPoint point)
    {
        _buffer[_head] = point;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// Read all current entries in chronological order (oldest → newest) into the
    /// caller-supplied span.  <paramref name="destination"/> must be at least Count elements.
    /// Returns the number of entries written.  No allocations.
    /// </summary>
    public int CopyTo(Span<TelemetryPoint> destination)
    {
        if (_count == 0) return 0;

        int tail = (_head - _count + Capacity) % Capacity;
        int written = 0;

        for (int i = 0; i < _count && written < destination.Length; i++)
        {
            destination[written++] = _buffer[(tail + i) % Capacity];
        }

        return written;
    }

    /// <summary>
    /// Compute rolling mean and population standard deviation over current entries.
    /// Uses a single-pass Welford algorithm — O(N), no intermediate allocations.
    ///
    /// Returns false if the buffer is empty (mean/stdDev set to 0).
    /// </summary>
    public bool TryComputeStats(out double mean, out double stdDev)
    {
        if (_count == 0)
        {
            mean = 0;
            stdDev = 0;
            return false;
        }

        // Welford's online algorithm — numerically stable, single pass
        double runningMean = 0;
        double runningM2 = 0;
        int n = 0;

        int tail = (_head - _count + Capacity) % Capacity;

        for (int i = 0; i < _count; i++)
        {
            double x = _buffer[(tail + i) % Capacity].Value;
            n++;
            double delta = x - runningMean;
            runningMean += delta / n;
            double delta2 = x - runningMean;
            runningM2 += delta * delta2;
        }

        mean = runningMean;
        // Population stddev (not sample): suits the fixed-window sensor use case
        stdDev = n > 1 ? Math.Sqrt(runningM2 / n) : 0.0;
        return true;
    }

    /// <summary>
    /// Peek at the most recently written point without removing it.
    /// Returns false if the buffer is empty.
    /// </summary>
    public bool TryPeekLatest(out TelemetryPoint point)
    {
        if (_count == 0)
        {
            point = default;
            return false;
        }

        int latestIndex = (_head - 1 + Capacity) % Capacity;
        point = _buffer[latestIndex];
        return true;
    }

    /// <summary>Reset the buffer to empty state without reallocating the array.</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        // Not clearing _buffer contents — stale values are masked by _count
    }
}
