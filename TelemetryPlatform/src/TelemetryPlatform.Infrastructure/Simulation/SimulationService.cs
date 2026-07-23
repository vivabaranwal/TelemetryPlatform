using System.Threading;
using Microsoft.Extensions.Logging;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Domain.ValueObjects;

namespace TelemetryPlatform.Infrastructure.Simulation;

/// <summary>
/// In-process simulation service.  Replicates the exact sensor physics from
/// tools/SensorSimulator/Program.cs but writes <see cref="TelemetryPoint"/>
/// structs directly into <see cref="ITelemetryChannel"/> — zero network hop.
///
/// Data enters the same Channel → TelemetryProcessingWorker → AnomalyDetection
/// → SignalR broadcast pipeline that the external simulator uses, unchanged.
///
/// Thread-safety contract:
///   Start() and Stop() are NOT reentrant but are safe to call from any thread.
///   All mutable state is protected by _lock or Interlocked.
/// </summary>
public sealed class SimulationService
{
    // ── Sensor profile (mirrors SensorSimulator/Program.cs exactly) ──────────

    private sealed record SensorProfile(
        string Label,
        double Midpoint,
        double Amplitude,
        double NoiseStdDev,
        double WaveFreqHz,
        double SpikeValue,
        double SpikeRange);

    private static SensorProfile GetProfile(short sensorId) => (sensorId % 3) switch
    {
        // Group 0: Hydraulic pump — pressure in psi
        0 => new SensorProfile(
            Label:       $"HydraulicPump-{sensorId}",
            Midpoint:    2000.0,
            Amplitude:   500.0,
            NoiseStdDev: 30.0,
            WaveFreqHz:  0.1,
            SpikeValue:  3600.0,
            SpikeRange:  400.0),

        // Group 1: Fuel flow regulator — scaled g/s
        1 => new SensorProfile(
            Label:       $"FuelRegulator-{sensorId}",
            Midpoint:    1500.0,
            Amplitude:   300.0,
            NoiseStdDev: 20.0,
            WaveFreqHz:  0.05,
            SpikeValue:  3700.0,
            SpikeRange:  300.0),

        // Group 2: Landing gear actuator — 0.01 mm increments
        _ => new SensorProfile(
            Label:       $"LandingGear-{sensorId}",
            Midpoint:    1800.0,
            Amplitude:   400.0,
            NoiseStdDev: 25.0,
            WaveFreqHz:  0.02,
            SpikeValue:  3550.0,
            SpikeRange:  200.0),
    };

    // ── Configuration ─────────────────────────────────────────────────────────

    private const int    SensorCount       = 10;
    private const int    Hz                = 100;
    private const double SpikeProbability  = 0.003;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly ITelemetryChannel _channel;
    private readonly ILogger<SimulationService> _logger;

    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task?                    _simulationTask;

    private DateTimeOffset? _startedAt;
    private long            _packetsSent;   // written via Interlocked

    // ── Constructor ───────────────────────────────────────────────────────────

    public SimulationService(ITelemetryChannel channel, ILogger<SimulationService> logger)
    {
        _channel = channel;
        _logger  = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the simulation loop is currently running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
                return _simulationTask is { IsCompleted: false };
        }
    }

    /// <summary>Snapshot of current simulation metrics for the status endpoint.</summary>
    public SimulationStatus GetStatus()
    {
        lock (_lock)
        {
            return new SimulationStatus(
                IsRunning:    _simulationTask is { IsCompleted: false },
                StartedAt:    _startedAt,
                PacketsSent:  Interlocked.Read(ref _packetsSent),
                SensorsActive: SensorCount);
        }
    }

    /// <summary>
    /// Starts the in-process simulation.
    /// </summary>
    /// <returns>
    ///   <c>true</c> if started successfully;
    ///   <c>false</c> if already running (caller should return 409).
    /// </returns>
    public bool Start()
    {
        lock (_lock)
        {
            if (_simulationTask is { IsCompleted: false })
            {
                _logger.LogWarning("SimulationService.Start() called while already running — ignored.");
                return false;
            }

            // Reset counters each fresh run
            Interlocked.Exchange(ref _packetsSent, 0);
            _startedAt = DateTimeOffset.UtcNow;

            _cts = new CancellationTokenSource();
            _simulationTask = RunAllSensorsAsync(_cts.Token);

            _logger.LogInformation(
                "In-process simulation started: {Sensors} sensors × {Hz} Hz = {Pps:N0} pkt/s",
                SensorCount, Hz, SensorCount * Hz);

            return true;
        }
    }

    /// <summary>
    /// Stops the simulation gracefully.  Waits for the current tick to complete.
    /// </summary>
    /// <returns>
    ///   <c>true</c> if stopped successfully;
    ///   <c>false</c> if not running (caller should return 409).
    /// </returns>
    public async Task<bool> StopAsync()
    {
        CancellationTokenSource? cts;
        Task?                    task;

        lock (_lock)
        {
            if (_simulationTask is null or { IsCompleted: true })
            {
                _logger.LogWarning("SimulationService.StopAsync() called while not running — ignored.");
                return false;
            }

            cts  = _cts;
            task = _simulationTask;
        }

        // Signal cancellation outside the lock to avoid deadlock
        cts?.Cancel();

        try
        {
            // Wait for the in-flight tick to finish — max 3s safety timeout
            await task!.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Simulation task did not stop within 3 s — forcing disposal.");
        }
        catch (OperationCanceledException) { /* expected */ }

        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }

        _logger.LogInformation(
            "In-process simulation stopped. Total packets sent: {Count:N0}",
            Interlocked.Read(ref _packetsSent));

        return true;
    }

    // ── Simulation loop ───────────────────────────────────────────────────────

    /// <summary>
    /// Launches one Task per sensor (all share the same CancellationToken).
    /// WhenAll is awaited so the top-level task completes only after all sensors exit.
    /// </summary>
    private async Task RunAllSensorsAsync(CancellationToken ct)
    {
        var tasks = Enumerable
            .Range(0, SensorCount)
            .Select(i => RunSensorAsync((short)i, ct))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — swallow so the host doesn't see an exception
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulation loop encountered an unexpected error.");
        }
    }

    /// <summary>
    /// Single-sensor simulation loop.  Mirrors RunSensorAsync in the console simulator
    /// but writes via <see cref="ITelemetryChannel.TryWrite"/> instead of WebSocket.
    /// </summary>
    private async Task RunSensorAsync(short sensorId, CancellationToken ct)
    {
        var rng          = new Random(sensorId * 31_337);   // deterministic seed per sensor
        var intervalMs   = 1000.0 / Hz;
        var nextTickMs   = (double)Environment.TickCount64;
        var profile      = GetProfile(sensorId);
        var phaseOffset  = rng.NextDouble() * 2 * Math.PI;  // stagger waves
        long packetIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            double now    = Environment.TickCount64;
            double waitMs = nextTickMs - now;

            if (waitMs > 0)
            {
                try
                {
                    await Task.Delay((int)waitMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;  // Clean exit — let in-flight tick complete naturally
                }
            }

            nextTickMs += intervalMs;

            // ── Generate realistic sensor value ───────────────────────────────

            double t       = packetIndex / (double)Hz;          // time in seconds
            double sine    = Math.Sin(2 * Math.PI * profile.WaveFreqHz * t + phaseOffset);
            double baseline = profile.Midpoint + sine * profile.Amplitude;

            // Box-Muller Gaussian noise
            double u1       = 1.0 - rng.NextDouble();
            double u2       = 1.0 - rng.NextDouble();
            double gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2 * Math.PI * u2);
            double value    = baseline + gaussian * profile.NoiseStdDev;

            // Spike injection
            short flags = 0;
            if (rng.NextDouble() < SpikeProbability)
            {
                value = profile.SpikeValue + rng.NextDouble() * profile.SpikeRange;
                flags = 0x01;
            }

            // ── Write directly into the telemetry channel ─────────────────────
            var point = TelemetryPoint.Now(sensorId, value, flags);
            _channel.TryWrite(point);

            Interlocked.Increment(ref _packetsSent);
            packetIndex++;
        }
    }
}

/// <summary>Immutable snapshot returned by the status endpoint.</summary>
public sealed record SimulationStatus(
    bool           IsRunning,
    DateTimeOffset? StartedAt,
    long           PacketsSent,
    int            SensorsActive);
