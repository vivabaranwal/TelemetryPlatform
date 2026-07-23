using FluentAssertions;
using TelemetryPlatform.Application.Buffers;
using TelemetryPlatform.Domain.ValueObjects;
using Xunit;

namespace TelemetryPlatform.Application.Tests;

/// <summary>
/// Unit tests for the RingBuffer — the core sliding-window store used by anomaly detection.
/// These tests are independent of all Infrastructure types (zero I/O, zero mocks needed).
/// </summary>
public sealed class RingBufferTests
{
    private static TelemetryPoint MakePoint(short sensorId, double value, long ts = 0) =>
        new(ts, sensorId, value);

    // ── Capacity & overflow ────────────────────────────────────────────────────

    [Fact]
    public void Write_WhenBelowCapacity_ShouldIncrementCount()
    {
        var buffer = new RingBuffer(5);
        buffer.Write(MakePoint(1, 100.0));
        buffer.Write(MakePoint(1, 200.0));

        buffer.Count.Should().Be(2);
        buffer.IsFull.Should().BeFalse();
    }

    [Fact]
    public void Write_WhenAtCapacity_ShouldOverwriteOldest()
    {
        var buffer = new RingBuffer(3);
        buffer.Write(MakePoint(1, 10.0));
        buffer.Write(MakePoint(1, 20.0));
        buffer.Write(MakePoint(1, 30.0));
        buffer.Write(MakePoint(1, 40.0)); // should overwrite 10.0

        buffer.Count.Should().Be(3);
        buffer.IsFull.Should().BeTrue();

        Span<TelemetryPoint> pts = stackalloc TelemetryPoint[3];
        buffer.CopyTo(pts);

        pts[0].Value.Should().Be(20.0, "oldest surviving should be 20");
        pts[1].Value.Should().Be(30.0);
        pts[2].Value.Should().Be(40.0, "newest should be 40");
    }

    [Fact]
    public void Write_ManyMoreThanCapacity_ShouldOnlyRetainLatestN()
    {
        const int capacity = 5;
        var buffer = new RingBuffer(capacity);

        for (int i = 1; i <= 100; i++)
            buffer.Write(MakePoint(1, i));

        buffer.Count.Should().Be(capacity);

        Span<TelemetryPoint> pts = stackalloc TelemetryPoint[capacity];
        buffer.CopyTo(pts);

        // Should contain values 96, 97, 98, 99, 100
        pts[0].Value.Should().Be(96.0);
        pts[4].Value.Should().Be(100.0);
    }

    // ── Statistics ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryComputeStats_EmptyBuffer_ShouldReturnFalse()
    {
        var buffer = new RingBuffer(10);
        bool ok = buffer.TryComputeStats(out double mean, out double stdDev);

        ok.Should().BeFalse();
        mean.Should().Be(0);
        stdDev.Should().Be(0);
    }

    [Fact]
    public void TryComputeStats_SingleElement_ShouldReturnMeanWithZeroStdDev()
    {
        var buffer = new RingBuffer(10);
        buffer.Write(MakePoint(1, 42.0));

        buffer.TryComputeStats(out double mean, out double stdDev).Should().BeTrue();

        mean.Should().BeApproximately(42.0, 0.001);
        stdDev.Should().Be(0.0);
    }

    [Fact]
    public void TryComputeStats_KnownData_ShouldMatchExpectedMeanAndStdDev()
    {
        // [2, 4, 4, 4, 5, 5, 7, 9] — textbook Welford example
        // Mean = 5, population stddev = 2
        var buffer = new RingBuffer(10);
        foreach (double v in new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })
            buffer.Write(MakePoint(1, v));

        buffer.TryComputeStats(out double mean, out double stdDev).Should().BeTrue();

        mean.Should().BeApproximately(5.0, 0.001);
        stdDev.Should().BeApproximately(2.0, 0.001);
    }

    [Fact]
    public void TryComputeStats_AfterOverwrite_ShouldReflectCurrentWindow()
    {
        var buffer = new RingBuffer(3);
        // Fill with high values
        buffer.Write(MakePoint(1, 1000.0));
        buffer.Write(MakePoint(1, 1000.0));
        buffer.Write(MakePoint(1, 1000.0));
        // Overwrite all with low values
        buffer.Write(MakePoint(1, 1.0));
        buffer.Write(MakePoint(1, 1.0));
        buffer.Write(MakePoint(1, 1.0));

        buffer.TryComputeStats(out double mean, out _).Should().BeTrue();
        mean.Should().BeApproximately(1.0, 0.001, "all high values should be gone");
    }

    // ── PeekLatest ────────────────────────────────────────────────────────────

    [Fact]
    public void TryPeekLatest_EmptyBuffer_ShouldReturnFalse()
    {
        var buffer = new RingBuffer(5);
        buffer.TryPeekLatest(out _).Should().BeFalse();
    }

    [Fact]
    public void TryPeekLatest_AfterWrite_ShouldReturnMostRecentValue()
    {
        var buffer = new RingBuffer(5);
        buffer.Write(MakePoint(1, 100.0));
        buffer.Write(MakePoint(1, 200.0));
        buffer.Write(MakePoint(1, 300.0));

        buffer.TryPeekLatest(out var latest).Should().BeTrue();
        latest.Value.Should().Be(300.0);
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_ShouldResetCountAndAllowReuse()
    {
        var buffer = new RingBuffer(5);
        for (int i = 0; i < 5; i++) buffer.Write(MakePoint(1, i));

        buffer.Clear();

        buffer.Count.Should().Be(0);
        buffer.IsFull.Should().BeFalse();
        buffer.TryPeekLatest(out _).Should().BeFalse();
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_ZeroCapacity_ShouldThrowArgumentOutOfRange()
    {
        var act = () => new RingBuffer(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
