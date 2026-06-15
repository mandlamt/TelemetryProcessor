# Distributed Telemetry Processor

A mock Industrial IoT system consisting of:

- **TelemetryPublisher** — ASP.NET Core Web API that generates sensor readings
  every second, holds the 10 most recent in memory, and spills overflow/aged
  readings to SQL Server.
- **TelemetryConsumer** — .NET Worker Service that polls the Publisher,
  computes a 5-point moving average per sensor type ("Complex Analysis"), and
  persists results to SQL Server.
- **Database** — SQL Server schema and stored procedures (`Database/schema.sql`).
- **TelemetryTests** — xUnit tests covering FIFO ordering and the moving
  average calculation.

## Prerequisites

- .NET 8 SDK
- Docker (for SQL Server) — or an existing SQL Server instance
- `sqlcmd` (optional, for running the schema script manually)

## 1. Start SQL Server (Docker)

```bash
docker compose up -d
```

This starts SQL Server 2022 on `localhost:1433` with:
- User: `sa`
- Password: `YourStrong!Passw0rd`

Wait ~20–30 seconds for the container to become healthy:

```bash
docker compose ps
```

## 2. Create the database and schema

Create the `TelemetryDb` database, then run the schema script:

```bash
# Create the database
docker exec -it telemetry-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "YourStrong!Passw0rd" -C \
  -Q "CREATE DATABASE TelemetryDb"

# Run the schema/stored procedures script
docker exec -i telemetry-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "YourStrong!Passw0rd" -C -d TelemetryDb \
  < Database/schema.sql
```

> The script is idempotent — tables and procedures are dropped and recreated
> if they already exist, so it's safe to re-run.

## 3. Run the Publisher

```bash
cd TelemetryPublisher
dotnet run
```

The Publisher listens on `http://localhost:5080` (see
`Properties/launchSettings.json`). Endpoints:

- `GET /api/telemetry/next` — oldest unconsumed reading (memory-first, then DB)
- `GET /api/telemetry/stats` — `{ queueDepth, totalGenerated, totalSpilled }`
- `GET /health` — health check

Swagger UI is available at `http://localhost:5080/swagger` in Development.

## 4. Run the Consumer

In a separate terminal:

```bash
cd TelemetryConsumer
dotnet run
```

The Consumer polls `http://localhost:5080` (configurable in
`appsettings.json` under `ConsumerOptions:PublisherBaseUrl`), processes each
reading with a 5-point moving average per sensor type, and writes results to
`AnalysisResults` via `sp_InsertAnalysisResults`.

## 5. Run the tests

```bash
cd TelemetryTests
dotnet test
```

## Configuration reference

### Publisher (`TelemetryPublisher/appsettings.json`)

| Key | Description | Default |
|---|---|---|
| `ConnectionStrings:TelemetryDb` | SQL Server connection string | see file |
| `PublisherOptions:GenerationIntervalSeconds` | How often new readings are generated | 1 |
| `PublisherOptions:InMemoryCapacity` | Max readings held in memory before spilling | 10 |
| `PublisherOptions:SpillAfterSeconds` | Max age before an in-memory reading spills | 5 |

### Consumer (`TelemetryConsumer/appsettings.json`)

| Key | Description | Default |
|---|---|---|
| `ConnectionStrings:TelemetryDb` | SQL Server connection string | see file |
| `ConsumerOptions:PublisherBaseUrl` | Publisher base URL | `http://localhost:5080` |
| `ConsumerOptions:PollingIntervalSeconds` | Poll interval when data is flowing | 1 |
| `ConsumerOptions:EmptyResponseBackoffSeconds` | Wait time after an empty response | 2 |
| `ConsumerOptions:MovingAverageWindowSize` | Sliding window size for analysis | 5 |
| `ConsumerOptions:RetryBaseDelaySeconds` | Base delay for Polly exponential backoff | 2 |
| `ConsumerOptions:RetryMaxAttempts` | Max retry attempts before logging a backoff | 5 |

## Quick smoke test

1. Start SQL Server and apply the schema (steps 1–2).
2. Start the Publisher; watch the console log a new `sr_NNNNNN` reading every second.
3. Hit `curl http://localhost:5080/api/telemetry/stats` to see counters increase.
4. Start the Consumer; watch it fetch readings, log a `MovingAverage` result, and persist it.
5. Stop the Publisher and confirm the Consumer logs retry/backoff warnings without crashing.
6. Restart the Publisher and confirm the Consumer resumes automatically.
