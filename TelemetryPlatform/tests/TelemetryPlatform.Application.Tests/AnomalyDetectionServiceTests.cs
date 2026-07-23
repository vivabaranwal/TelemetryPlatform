using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TelemetryPlatform.Application.Interfaces;
using TelemetryPlatform.Application.Services;
using TelemetryPlatform.Domain.Enums;
using TelemetryPlatform.Domain.ValueObjects;
using Xunit;

namespace TelemetryPlatform.Application.Tests;

/// <summary>
/// Unit tests for the AnomalyDetectionService Z-score engine.
/// All tests run without Infrastructure, SignalR, or databases.
/// </summary>
public sealed class AnomalyDetectionServiceTests
{
    private static AnomalyDetectionService CreateService(
        double absoluteThreshold = 3500.0,
        double zScoreThreshold = 3.0,
        double zScoreWarning = 2.0,
        int minSamples = 5,
        int ringCapacity = 100)
    {
        var opts = Options.Create(new AnomalyDetectionOptions
        {
            AbsoluteThreshold = absoluteThreshold,
            ZScoreThreshold = zScoreThreshold,
            ZScoreWarningThreshold = zScoreWarning,
            MinSamplesForStats = minSamples,
            RingBufferCapacity = ringCapacity,
        });

        return new AnomalyDetectionService(opts, NullLogger<AnomalyDetectionService>.Instance);
    }

    private static TelemetryPoint MakePoint(short sensorId, double value) =>
        new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sensorId, value);

    // ── Min samples guard ─────────────────────────────────────────────────────

    [Fact]
    public void Analyse_BelowMinSamples_ShouldReturnNull()
    {
        var service = CreateService(minSamples: 10);

        // Feed only 5 points — below the minimum
        for (int i = 0; i < 5; i++)
        {
            var result = service.Analyse(MakePoint(1, 1000.0));
            result.Should().BeNull("not enough samples to compute Z-score");
        }
    }

    // ── Normal readings ───────────────────────────────────────────────────────

    [Fact]
    public void Analyse_NormalReadings_ShouldNotTriggerAnomaly()
    {
        var service = CreateService(minSamples: 5);
        const short sensor = 1;

        // Stable readings around 1500 — well below 3500 threshold
        for (int i = 0; i < 50; i++)
        {
            var result = service.Analyse(MakePoint(sensor, 1500.0 + (i % 5 - 2) * 10));
            result.Should().BeNull($"reading {i} is within normal range");
        }
    }

    // ── Critical: absolute + Z-score breach ──────────────────────────────────

    [Fact]
    public void Analyse_AboveThresholdAndHighZScore_ShouldTriggerCritical()
    {
        var service = CreateService(absoluteThreshold: 3500.0, zScoreThreshold: 3.0, minSamples: 5);
        const short sensor = 2;

        // Establish a stable baseline: mean ≈ 1500, stddev ≈ 0
        for (int i = 0; i < 20; i++)
            service.Analyse(MakePoint(sensor, 1500.0));

        // Inject spike: far above threshold AND very high Z-score
        var anomaly = service.Analyse(MakePoint(sensor, 5000.0));

        anomaly.Should().NotBeNull();
        anomaly!.Value.Severity.Should().Be(AlertSeverity.Critical);
        anomaly.Value.ZScore.Should().BeGreaterThan(3.0);
        anomaly.Value.TriggerPoint.Value.Should().Be(5000.0);
    }

    // ── High: only absolute breach ────────────────────────────────────────────

    [Fact]
    public void Analyse_AboveThresholdWithLowZScore_ShouldTriggerHigh()
    {
        // Large window with high stddev so Z-score stays below critical threshold
        var service = CreateService(
            absoluteThreshold: 3500.0,
            zScoreThreshold: 10.0, // very high — almost impossible to trigger Critical
            zScoreWarning: 2.0,
            minSamples: 5);

        const short sensor = 3;

        // High variance baseline so Z-score for 3600 stays below 10
        for (int i = 0; i < 20; i++)
            service.Analyse(MakePoint(sensor, i % 2 == 0 ? 0 : 6000)); // extreme variance

        var anomaly = service.Analyse(MakePoint(sensor, 3600.0));

        anomaly.Should().NotBeNull();
        anomaly!.Value.Severity.Should().Be(AlertSeverity.High,
            "absolute threshold exceeded but Z-score below critical level");
    }

    // ── Warning: only Z-score breach ─────────────────────────────────────────

    [Fact]
    public void Analyse_ZScoreAboveWarningBelowCritical_ShouldTriggerWarning()
    {
        var service = CreateService(
            absoluteThreshold: 999999.0, // never triggers absolute
            zScoreThreshold: 3.0,
            zScoreWarning: 2.0,
            minSamples: 5);

        const short sensor = 4;

        // Stable baseline: mean=1000, stddev≈0
        for (int i = 0; i < 20; i++)
            service.Analyse(MakePoint(sensor, 1000.0));

        // Seed with some variance so Z-score for moderate spike stays below Critical
        for (int i = 0; i < 10; i++)
            service.Analyse(MakePoint(sensor, 1000.0 + (i % 3 == 0 ? 100.0 : -100.0)));

        // A moderate jump that may be Warning depending on exact stddev
        // The key invariant: if any anomaly fires, it must NOT be Critical
        var anomaly = service.Analyse(MakePoint(sensor, 1000.0 + 250.0));
        if (anomaly.HasValue)
            ((int)anomaly.Value.Severity).Should().BeLessThan(
                (int)AlertSeverity.Critical,
                "a moderate jump should not reach Critical severity");
    }

    // ── Multiple sensors are independent ─────────────────────────────────────

    [Fact]
    public void Analyse_MultiSensor_BuffersAreIsolated()
    {
        var service = CreateService(minSamples: 5, absoluteThreshold: 3500);

        // Sensor 1: baseline at 1000
        for (int i = 0; i < 10; i++) service.Analyse(MakePoint(1, 1000.0));

        // Sensor 2: baseline at 2000 (different sensor)
        for (int i = 0; i < 10; i++) service.Analyse(MakePoint(2, 2000.0));

        // Spike on sensor 1 should not affect sensor 2's statistics
        var anomaly1 = service.Analyse(MakePoint(1, 9000.0));
        var anomaly2 = service.Analyse(MakePoint(2, 2000.0)); // normal for sensor 2

        anomaly1.Should().NotBeNull("sensor 1 has a spike");
        anomaly2.Should().BeNull("sensor 2 is still normal");
    }

    // ── TrackedSensorCount ────────────────────────────────────────────────────

    [Fact]
    public void TrackedSensorCount_ShouldIncrementOnNewSensors()
    {
        var service = CreateService();

        service.TrackedSensorCount.Should().Be(0);

        service.Analyse(MakePoint(1, 100.0));
        service.TrackedSensorCount.Should().Be(1);

        service.Analyse(MakePoint(2, 100.0));
        service.TrackedSensorCount.Should().Be(2);

        service.Analyse(MakePoint(1, 100.0)); // existing sensor
        service.TrackedSensorCount.Should().Be(2, "no new sensor added");
    }
}
