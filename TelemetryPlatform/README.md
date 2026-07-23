# TelemetryPlatform

Backend .NET 8 solution for ultra-high-throughput aerospace IoT telemetry pipeline and real-time anomaly detection.

---

## Solution Structure

```
TelemetryPlatform/
├── src/
│   ├── TelemetryPlatform.Domain/          -- TelemetryPoint struct, domain interfaces
│   ├── TelemetryPlatform.Application/     -- Anomaly detection, ring buffer, DTOs
│   ├── TelemetryPlatform.Infrastructure/  -- Channel singleton, SignalR hub, BulkCopyWriter, SimulationService
│   └── TelemetryPlatform.Api/             -- Composition root, controllers, health endpoint
├── tools/SensorSimulator/                 -- Standalone packet generator CLI
└── tests/                                 -- Unit and integration test suites
```

Dependency rule enforced by project references:
```
Api -> Infrastructure -> Application -> Domain
```
Domain has zero outward references. Application never references Infrastructure.

---

## Running the Backend API

```bash
dotnet restore TelemetryPlatform.sln
dotnet build TelemetryPlatform.sln
dotnet run --project src/TelemetryPlatform.Api
```

- API Base URL: `http://localhost:5000`
- SignalR Hub: `ws://localhost:5000/hubs/telemetry`
- Swagger Docs: `http://localhost:5000/swagger`

---

## Running Tests

```bash
dotnet test TelemetryPlatform.sln --verbosity normal
```
