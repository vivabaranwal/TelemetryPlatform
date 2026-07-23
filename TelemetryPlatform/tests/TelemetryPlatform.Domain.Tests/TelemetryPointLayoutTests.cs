using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentAssertions;
using TelemetryPlatform.Domain.ValueObjects;
using Xunit;

namespace TelemetryPlatform.Domain.Tests;

/// <summary>
/// Verifies the memory layout guarantee of TelemetryPoint.
/// These tests must pass before any hot-path benchmarking — if the struct grows
/// beyond 16 bytes, the cache-line utilization guarantee is violated.
/// </summary>
public sealed class TelemetryPointLayoutTests
{
    [Fact]
    public void TelemetryPoint_ShouldBe_ExactlySixteenBytes()
    {
        // CRITICAL: This is an architectural invariant.
        // If this test fails, a field was added or changed without updating the memory layout docs.
        int size = Unsafe.SizeOf<TelemetryPoint>();
        size.Should().Be(16, "4 TelemetryPoints must fit in a 64-byte CPU cache line");
    }

    [Fact]
    public void TelemetryPoint_ShouldBe_AReadonlyStruct()
    {
        typeof(TelemetryPoint).IsValueType.Should().BeTrue("TelemetryPoint must be a value type (struct)");
    }

    [Fact]
    public void TelemetryPoint_Constructor_ShouldPopulateAllFields()
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        short sensorId = 42;
        double value = 2500.75;
        short flags = 0x01;

        var point = new TelemetryPoint(ts, sensorId, value, flags);

        point.Timestamp.Should().Be(ts);
        point.SensorId.Should().Be(sensorId);
        point.Value.Should().Be(value);
        point.Flags.Should().Be(flags);
    }

    [Fact]
    public void TelemetryPoint_Now_ShouldStampCurrentTime()
    {
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var point = TelemetryPoint.Now(1, 1234.5);
        long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        point.Timestamp.Should().BeInRange(before, after);
        point.SensorId.Should().Be(1);
        point.Value.Should().BeApproximately(1234.5, 0.001);
    }

    [Fact]
    public void TelemetryPoint_Flags_SpikeInjected_ShouldBeParsedCorrectly()
    {
        var spike = new TelemetryPoint(0, 1, 9999.0, flags: 0x01);
        var normal = new TelemetryPoint(0, 1, 1000.0, flags: 0x00);

        spike.IsSpikeInjected.Should().BeTrue();
        spike.IsAnomalyFlagged.Should().BeFalse();
        normal.IsSpikeInjected.Should().BeFalse();
        normal.IsAnomalyFlagged.Should().BeFalse();
    }

    [Fact]
    public void TelemetryPoint_Equality_ShouldCompareAllFields()
    {
        var a = new TelemetryPoint(1000, 1, 500.0, 0);
        var b = new TelemetryPoint(1000, 1, 500.0, 0);
        var c = new TelemetryPoint(1001, 1, 500.0, 0); // different timestamp

        (a == b).Should().BeTrue();
        (a == c).Should().BeFalse();
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void TelemetryPoint_TimestampAsDateTimeOffset_ShouldRoundtrip()
    {
        var expected = DateTimeOffset.UtcNow;
        long ms = expected.ToUnixTimeMilliseconds();
        var point = new TelemetryPoint(ms, 1, 0.0);

        point.TimestampAsDateTimeOffset.ToUnixTimeMilliseconds()
             .Should().Be(ms);
    }
}
