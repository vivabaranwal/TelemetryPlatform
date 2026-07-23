# TelemetryPlatform

Production-grade, high-throughput IoT telemetry processing pipeline and real-time dashboard engine for aerospace component testing (hydraulic pumps, fuel flow regulators, landing gear actuators).

Built with .NET 8, ASP.NET Core SignalR, PostgreSQL, and a React + Vite + uPlot front-end dashboard.

---

## Architecture Overview

```
+-----------------------------------------------------------------------------+
|                               FRONTEND LAYER                                |
|                                                                             |
|   React 19 + TypeScript + Vite + Tailwind CSS + uPlot + SheetJS (XLSX)      |
|   - Real-time streaming charts (60 FPS direct canvas updates)               |
|   - Visual threshold indicators and critical zone shading                   |
|   - In-process simulation control HUD button                                |
|   - Per-sensor warning history drawer with severity/duration filters        |
|   - Excel data export capability                                            |
+-----------------------------------------------------------------------------+
                                       ^
                                       | WebSockets / SignalR
                                       v
+-----------------------------------------------------------------------------+
|                                BACKEND API                                  |
|                                                                             |
|   ASP.NET Core Web API + SignalR TelemetryHub                               |
|   - POST /api/simulation/start | stop | status                            |
|   - GET  /health                                                            |
|   - SignalR Hub: ws://localhost:5000/hubs/telemetry                         |
+-----------------------------------------------------------------------------+
                                       |
                                       v
+-----------------------------------------------------------------------------+
|                             IN-MEMORY PIPELINE                              |
|                                                                             |
|   ITelemetryChannel (System.Threading.Channels.Channel<TelemetryPoint>)     |
|   - Capacity: 8,192 items, BoundedChannelFullMode.DropOldest                |
|   - Decouples incoming telemetry ingestion from worker processing           |
+-----------------------------------------------------------------------------+
                                       |
                                       v
+-----------------------------------------------------------------------------+
|                           PROCESSING WORKER                                 |
|                                                                             |
|   TelemetryProcessingWorker (IHostedService)                                |
|   - AnomalyDetectionService: Welford online algorithm (Z-Score + Absolute) |
|   - SignalR Alert Broadcaster                                               |
|   - BulkCopyWriter: High-performance PostgreSQL binary COPY persistence     |
+-----------------------------------------------------------------------------+
                                       |
                                       v
+-----------------------------------------------------------------------------+
|                             DATABASE LAYER                                  |
|                                                                             |
|   PostgreSQL time-series table (sensor_readings)                            |
+-----------------------------------------------------------------------------+
```

---

## Project Structure

```
HAL_project/
├── .gitignore
├── README.md
├── TelemetryPlatform/                  # .NET 8 Backend Solution
│   ├── TelemetryPlatform.sln
│   ├── src/
│   │   ├── TelemetryPlatform.Domain/          # Core domain models (TelemetryPoint, interfaces)
│   │   ├── TelemetryPlatform.Application/     # Anomaly detection, RingBuffer, Welford engine
│   │   ├── TelemetryPlatform.Infrastructure/  # Channel singleton, SignalR hub, BulkCopyWriter, SimulationService
│   │   └── TelemetryPlatform.Api/             # ASP.NET Core API controllers, composition root
│   ├── tools/
│   │   └── SensorSimulator/                   # Standalone CLI sensor simulation executable
│   └── tests/
│       ├── TelemetryPlatform.Domain.Tests/
│       ├── TelemetryPlatform.Application.Tests/
│       └── TelemetryPlatform.Infrastructure.Tests/
└── telemetry-dashboard/                # React Front-End Dashboard
    ├── package.json
    ├── vite.config.ts
    └── src/
        ├── App.tsx                        # Main dashboard layout and HUD
        ├── components/
        │   ├── StreamingChart.tsx         # High-performance uPlot chart component
        │   ├── AlertFeed.tsx              # Real-time anomaly notification feed
        │   ├── WarningPanel.tsx           # Per-sensor warning history drawer
        │   └── SimulationButton.tsx       # Start/stop simulation control HUD button
        └── services/
            ├── signalr.ts                 # SignalR hub connection manager
            └── warningStore.ts            # Zero-render module-level alert store
```

---

## Key Features

### 1. High-Throughput Telemetry Pipeline
- 16-byte memory layout for TelemetryPoint struct (4 points per 64-byte CPU cache line).
- Thread-safe bounded channel buffer configured with DropOldest for low-latency real-time processing.
- PostgreSQL binary COPY writer for database ingestion.

### 2. Real-Time Anomaly Detection
- Welford's algorithm calculates running mean and standard deviation in O(1) time without heap allocations.
- Dual-trigger alert evaluation:
  - Z-Score threshold (Warning >= 2.0, Critical >= 3.0).
  - Absolute value threshold (e.g., Hydraulic Pump pressure > 3,500 psi).

### 3. Dashboard Visuals and Performance
- Canvas-rendered uPlot streaming charts updating at 60 FPS via direct DOM mutation.
- Canvas-based threshold indicators (dashed critical threshold line, shaded critical region, labels).
- Modular design keeping packet handling separated from React state re-renders.

### 4. Per-Sensor Warning History and Excel Export
- Per-sensor warning button with live badge counter.
- Slide-out Warning Panel with duration filter (1min, 5min, 15min, All) and severity toggles (WARNING, CRITICAL).
- Excel export (.xlsx) generating Warnings and Summary sheets via SheetJS.

### 5. In-Process Simulation Control
- Dashboard HUD button to start and stop simulation via REST API.
- Back-end SimulationService running multi-sensor waveform physics in-process.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend Framework | .NET 8, ASP.NET Core Web API |
| Real-Time Transport | ASP.NET Core SignalR (WebSockets) |
| Database | PostgreSQL 15+ via Npgsql |
| Frontend Framework | React 19, TypeScript, Vite |
| Styling | Tailwind CSS |
| Charting | uPlot |
| Data Export | SheetJS (xlsx) |
| Icons | Lucide React |

---

## Prerequisites

- .NET 8.0 SDK
- Node.js 18+ and npm
- PostgreSQL 15+ (required if database persistence is enabled)

---

## Setup and Installation

### 1. Repository Setup
```bash
git clone https://github.com/vivabaranwal/TelemetryPlatform.git
cd TelemetryPlatform
```

### 2. Backend Setup
```bash
cd TelemetryPlatform
dotnet restore TelemetryPlatform.sln
dotnet build TelemetryPlatform.sln
```

Set your PostgreSQL connection string in `TelemetryPlatform/src/TelemetryPlatform.Api/appsettings.json` or via user secrets:
```json
{
  "ConnectionStrings": {
    "TelemetryDb": "Host=localhost;Port=5432;Database=telemetry_db;Username=postgres;Password=your_password"
  }
}
```

Start the API server:
```bash
dotnet run --project src/TelemetryPlatform.Api
```

Backend URLs:
- API Server: `http://localhost:5000`
- Swagger UI: `http://localhost:5000/swagger`
- SignalR Hub: `ws://localhost:5000/hubs/telemetry`

### 3. Frontend Setup
In a new terminal window:
```bash
cd telemetry-dashboard
npm install
npm run dev
```

Dashboard URL:
- Dashboard: `http://localhost:5173`

---

## Running the Simulation

### Option A: Via Dashboard UI (Recommended)
1. Open `http://localhost:5173`.
2. Click the green **Start Simulation** button in the top HUD bar.
3. The button turns red (**Stop Simulation**), and telemetry data streams into the charts.
4. Click **Stop Simulation** to pause data streaming.

### Option B: Standalone CLI Tool
Alternatively, run the console simulator directly:
```bash
cd TelemetryPlatform
dotnet run --project tools/SensorSimulator -- --sensors 10 --hz 100 --url ws://localhost:5000/hubs/telemetry
```

CLI Parameters:
- `--sensors`: Number of simulated sensors (Default: 10).
- `--hz`: Sampling rate per sensor in Hz (Default: 100).
- `--spike-probability`: Per-packet anomaly spike probability (Default: 0.003).

---

## Sensor Profiles and Thresholds

| Sensor ID | Sensor Name | Engineering Unit | Normal Range | Critical Threshold |
|---|---|---|---|---|
| 0 | Hydraulic Pump A | psi | 1,500 - 2,500 | 3,500 psi |
| 1 | Fuel Regulator 1 | g/s | 1,200 - 1,800 | 1,800 g/s |
| 2 | Landing Gear Actuator | mm | 1,400 - 2,200 | 3,200 mm |
| 3 | Hydraulic Pump B | psi | 1,500 - 2,500 | 3,500 psi |
| 4 | Fuel Regulator 2 | g/s | 1,200 - 1,800 | 1,800 g/s |
| 5 | Aileron Actuator L | mm | 1,400 - 2,200 | 3,000 mm |

---

## API Endpoints

### Simulation Control
- `GET /api/simulation/status` - Returns current running state, started timestamp, packets sent, and active sensor count.
- `POST /api/simulation/start` - Starts the in-process simulation loop. Returns 409 Conflict if already running.
- `POST /api/simulation/stop` - Stops the simulation loop gracefully. Returns 409 Conflict if already stopped.

### Diagnostics
- `GET /health` - Returns service status, channel buffer depth, total packets received, and total packets dropped.

---

## Verification and Testing

Execute backend automated test suites:
```bash
cd TelemetryPlatform
dotnet test TelemetryPlatform.sln --verbosity normal
```

Test projects included:
- `TelemetryPlatform.Domain.Tests`: Memory layout and struct invariant tests.
- `TelemetryPlatform.Application.Tests`: RingBuffer and Welford algorithm tests.
- `TelemetryPlatform.Infrastructure.Tests`: Channel throughput and integration tests.

---

## License

This project is licensed under the MIT License.
