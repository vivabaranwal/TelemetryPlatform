using System.CommandLine;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SensorSimulator;

/// <summary>
/// Wire format for telemetry packets — mirrors TelemetryPacketDto exactly.
/// Defined inline here so the simulator is fully self-contained (zero project references).
/// </summary>
internal sealed record SimPacket(
    long Timestamp,
    short SensorId,
    double Value,
    short Flags,
    string Label
);

/// <summary>
/// Simulates multiple concurrent aerospace sensors streaming telemetry via WebSocket
/// to the TelemetryPlatform backend.
///
/// Signal profiles:
///   Hydraulic pump   : sine wave 1500–3000 psi + Gaussian noise
///   Fuel regulator   : sine wave 0.5–2.5 kg/s + Gaussian noise
///   Landing gear     : sine wave 0–100 mm displacement + Gaussian noise
///   All sensors      : configurable random spike injection for anomaly testing
///
/// Usage:
///   dotnet run --project tools/SensorSimulator -- --sensors 10 --hz 100 --url ws://localhost:5000/hubs/telemetry
///   dotnet run --project tools/SensorSimulator -- --sensors 3 --hz 50 --spike-probability 0.005
/// </summary>
internal static class Program
{
    // JSON serializer options: camelCase to match SignalR's default hub protocol
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static async Task<int> Main(string[] args)
    {
        // ── CLI argument definitions ──────────────────────────────────────────
        var sensorsOpt = new Option<int>(
            name: "--sensors",
            description: "Number of concurrent simulated sensors",
            getDefaultValue: () => 10);

        var hzOpt = new Option<int>(
            name: "--hz",
            description: "Packets per second per sensor",
            getDefaultValue: () => 100);

        var urlOpt = new Option<string>(
            name: "--url",
            description: "WebSocket URL of the TelemetryHub",
            getDefaultValue: () => "ws://localhost:5000/hubs/telemetry");

        var spikeOpt = new Option<double>(
            name: "--spike-probability",
            description: "Probability (0.0–1.0) of injecting a spike value per packet",
            getDefaultValue: () => 0.003);

        var durationOpt = new Option<int>(
            name: "--duration",
            description: "Run duration in seconds. 0 = run forever",
            getDefaultValue: () => 0);

        var rootCommand = new RootCommand("Aerospace sensor telemetry simulator")
        {
            sensorsOpt, hzOpt, urlOpt, spikeOpt, durationOpt
        };

        rootCommand.SetHandler(
            async (sensors, hz, url, spike, duration) =>
                await RunSimulatorAsync(sensors, hz, url, spike, duration),
            sensorsOpt, hzOpt, urlOpt, spikeOpt, durationOpt);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task RunSimulatorAsync(
        int sensorCount,
        int hz,
        string hubUrl,
        double spikeProbability,
        int durationSeconds)
    {
        using var cts = new CancellationTokenSource();

        if (durationSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║     Aerospace Sensor Telemetry Simulator             ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝");
        Console.WriteLine($"  Sensors          : {sensorCount}");
        Console.WriteLine($"  Rate per sensor  : {hz} Hz");
        Console.WriteLine($"  Total throughput : {sensorCount * hz:N0} packets/sec");
        Console.WriteLine($"  Target URL       : {hubUrl}");
        Console.WriteLine($"  Spike probability: {spikeProbability:P2}");
        Console.WriteLine($"  Duration         : {(durationSeconds > 0 ? $"{durationSeconds}s" : "∞ (Ctrl+C to stop)")}");
        Console.WriteLine();

        // ── Connect WebSocket ─────────────────────────────────────────────────
        // All sensors share one WebSocket connection — SignalR multiplexes over it.
        // In a real rig, each physical device would have its own connection.
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "AerospaceSensorSimulator/1.0");

        try
        {
            Console.Write($"  Connecting to {hubUrl}...");

            // SignalR negotiation: send protocol handshake after connecting
            await ws.ConnectAsync(new Uri(hubUrl), cts.Token);
            await PerformSignalRHandshakeAsync(ws, cts.Token);

            Console.WriteLine(" ✓ Connected");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n  ✗ Connection failed: {ex.Message}");
            Console.Error.WriteLine("  Is the backend running? (dotnet run --project src/TelemetryPlatform.Api)");
            return;
        }

        // ── Launch per-sensor tasks ───────────────────────────────────────────
        var sensorTasks = Enumerable.Range(0, sensorCount)
            .Select(i => RunSensorAsync(
                ws: ws,
                sensorId: (short)i,
                hz: hz,
                spikeProbability: spikeProbability,
                ct: cts.Token))
            .ToArray();

        // Stats reporter task
        var statsTask = ReportStatsAsync(sensorCount, hz, cts.Token);

        // Background read task to drain incoming SignalR broadcasts (prevents TCP backpressure/buffer fill)
        var readTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* Ignore */ }
        }, cts.Token);

        await Task.WhenAll(Task.WhenAll(sensorTasks), statsTask, readTask);

        // ── Graceful shutdown ─────────────────────────────────────────────────
        if (ws.State == WebSocketState.Open)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Simulator shutdown", default);
        }

        Console.WriteLine("\n  Simulator stopped.");
    }

    /// <summary>
    /// Send the SignalR WebSocket protocol handshake.
    /// Without this, the server closes the connection immediately.
    /// </summary>
    private static async Task PerformSignalRHandshakeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // SignalR JSON protocol handshake: {"protocol":"json","version":1}\x1e
        const string handshake = "{\"protocol\":\"json\",\"version\":1}\u001e";
        var handshakeBytes = Encoding.UTF8.GetBytes(handshake);
        await ws.SendAsync(handshakeBytes, WebSocketMessageType.Text, true, ct);

        // Read server's handshake acknowledgement
        var buffer = new byte[256];
        await ws.ReceiveAsync(buffer, ct);
        // Handshake response is: {}\x1e — we don't need to inspect it
    }

    /// <summary>
    /// Simulates a single sensor at the specified Hz, streaming packets via the shared WebSocket.
    /// </summary>
    private static async Task RunSensorAsync(
        ClientWebSocket ws,
        short sensorId,
        int hz,
        double spikeProbability,
        CancellationToken ct)
    {
        var rng = new Random(sensorId * 31337); // Deterministic seed per sensor
        var intervalMs = 1000.0 / hz;
        var nextTick = Environment.TickCount64;
        var profile = GetSensorProfile(sensorId);

        double phaseOffset = rng.NextDouble() * 2 * Math.PI; // Stagger sensor waves
        long packetIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            long waitMs = nextTick - now;

            if (waitMs > 0)
                await Task.Delay((int)waitMs, ct).ConfigureAwait(false);

            nextTick += (long)intervalMs;

            // ── Generate realistic sensor value ───────────────────────────
            double t = packetIndex / (double)hz; // time in seconds
            double sine = Math.Sin(2 * Math.PI * profile.WaveFreqHz * t + phaseOffset);
            double baseline = profile.Midpoint + sine * profile.Amplitude;

            // Gaussian noise: Box-Muller transform
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2 * Math.PI * u2);
            double noise = gaussian * profile.NoiseStdDev;

            double value = baseline + noise;

            // Spike injection (anomaly testing)
            short flags = 0;
            if (rng.NextDouble() < spikeProbability)
            {
                value = profile.SpikeValue + rng.NextDouble() * profile.SpikeRange;
                flags = 0x01; // spike flag
            }

            // ── Build SignalR invocation message ──────────────────────────
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var packet = new SimPacket(timestamp, sensorId, value, flags, profile.Label);
            await SendSignalRMessageAsync(ws, "SendTelemetry", packet, ct);

            packetIndex++;
        }
    }

    /// <summary>
    /// Wraps a payload in the SignalR JSON hub protocol message format and sends it.
    /// Format: {"type":1,"target":"MethodName","arguments":[...]}\x1e
    /// </summary>
    private static async Task SendSignalRMessageAsync<T>(
        ClientWebSocket ws,
        string method,
        T payload,
        CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;

        // SignalR JSON protocol: type 1 = Invocation
        var message = new
        {
            type = 1,
            target = method,
            arguments = new object[] { payload! },
        };

        // Serialize + append the SignalR record separator (0x1e)
        string json = JsonSerializer.Serialize(message, JsonOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\u001e");

        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (WebSocketException)
        {
            // Connection dropped — the task will exit on next ct check
        }
    }

    /// <summary>Print a throughput stats line every 5 seconds.</summary>
    private static async Task ReportStatsAsync(int sensorCount, int hz, CancellationToken ct)
    {
        long expectedPps = (long)sensorCount * hz;
        long iteration = 0;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }

            iteration++;
            Console.WriteLine(
                $"  [{DateTimeOffset.UtcNow:HH:mm:ss}] " +
                $"Tick #{iteration * 5}s | " +
                $"Target: {expectedPps:N0} pkt/s | " +
                $"Sensors active: {sensorCount}");
        }
    }

    // ── Sensor profiles ───────────────────────────────────────────────────────

    private record SensorProfile(
        string Label,
        string Unit,
        double Midpoint,
        double Amplitude,
        double NoiseStdDev,
        double WaveFreqHz,
        double SpikeValue,
        double SpikeRange);

    private static SensorProfile GetSensorProfile(short sensorId) => (sensorId % 3) switch
    {
        // Group 0: Hydraulic pump — pressure in psi
        0 => new SensorProfile(
            Label: $"HydraulicPump-{sensorId}",
            Unit: "psi",
            Midpoint: 2000.0,
            Amplitude: 500.0,
            NoiseStdDev: 30.0,
            WaveFreqHz: 0.1,
            SpikeValue: 3600.0,   // exceeds 3500 psi threshold → triggers Critical
            SpikeRange: 400.0),

        // Group 1: Fuel flow regulator — flow rate in kg/s (scaled ×1000 for psi range)
        1 => new SensorProfile(
            Label: $"FuelRegulator-{sensorId}",
            Unit: "g/s",
            Midpoint: 1500.0,
            Amplitude: 300.0,
            NoiseStdDev: 20.0,
            WaveFreqHz: 0.05,
            SpikeValue: 3700.0,
            SpikeRange: 300.0),

        // Group 2: Landing gear actuator — position in 0.01mm increments
        _ => new SensorProfile(
            Label: $"LandingGear-{sensorId}",
            Unit: "0.01mm",
            Midpoint: 1800.0,
            Amplitude: 400.0,
            NoiseStdDev: 25.0,
            WaveFreqHz: 0.02,
            SpikeValue: 3550.0,
            SpikeRange: 200.0),
    };
}
