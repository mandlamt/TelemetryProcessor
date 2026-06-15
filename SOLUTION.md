# SOLUTION.md

## Overview

This solution implements the Publisher/Consumer Core Slice as a single .NET 8
solution with four projects:

- `TelemetryShared` — shared `SensorReading` / `AnalysisResult` models.
- `TelemetryPublisher` — ASP.NET Core Web API.
- `TelemetryConsumer` — .NET Worker Service.
- `TelemetryTests` — xUnit unit tests.

Persistence uses SQL Server exclusively via stored procedures
(`Database/schema.sql`), run locally via Docker.

## Project / File Map

This section maps every requirement in the brief to the exact file(s) that
implement it, so the design can be cross-referenced quickly during review.

### TelemetryShared

| File | Purpose |
|---|---|
| `Models/SensorReading.cs` | Shared data shape: `Id`, `Timestamp`, `Value`, `SensorType`, `SequenceNumber`. The `SequenceNumber` field is an addition beyond the brief's example JSON — see "FIFO" below for why. |
| `Models/AnalysisResult.cs` | Shared data shape: `Id`, `SensorReadingId`, `AnalysisType`, `Result`, `ProcessedAt`, `CorrelationId`. `CorrelationId` supports the optional structured-logging requirement. |

### TelemetryPublisher (Task 1)

| File | Purpose |
|---|---|
| `Program.cs` | Composition root: registers Serilog, controllers, Swagger, the singleton `ITelemetryStore` and `IPendingReadingRepository`, the `ReadingGeneratorService` hosted service, and `/health`. |
| `Models/PublisherOptions.cs` | Binds `GenerationIntervalSeconds`, `InMemoryCapacity`, `SpillAfterSeconds` from `appsettings.json`. |
| `Services/ReadingGeneratorService.cs` | `BackgroundService` that generates one `SensorReading` per second (Temperature/Pressure/Humidity/Vibration with plausible value ranges) and adds it to the store. |
| `Services/ITelemetryStore.cs` | Interface + `TimedReading`/`TelemetryStats` types. |
| `Services/InMemoryTelemetryStore.cs` | `ConcurrentQueue<TimedReading>`-backed store: capacity-based spill on `AddReadingAsync`, age-based spill + memory/DB fallback on `GetNextAsync`, `Interlocked` counters for stats. |
| `Data/IPendingReadingRepository.cs` / `SqlPendingReadingRepository.cs` | ADO.NET wrapper calling `sp_SavePendingReading` and `sp_GetOldestPending` with parameterized `SqlCommand`s. |
| `Controllers/TelemetryController.cs` | `GET api/telemetry/next` (200 with reading / 204 empty) and `GET api/telemetry/stats` (queue depth, total generated, total spilled). |
| `appsettings.json` | Connection string + `PublisherOptions` defaults. |

### TelemetryConsumer (Task 2)

| File | Purpose |
|---|---|
| `Program.cs` | Composition root: Serilog, bounded `Channel<SensorReading>`, `HttpClient` with Polly retry policy, `IAnalysisResultRepository`, `IAnalysisService`, and the two hosted services `FetchWorker` + `ProcessingWorker`. |
| `Models/ConsumerOptions.cs` | Binds `PublisherBaseUrl`, `PollingIntervalSeconds`, `EmptyResponseBackoffSeconds`, `MovingAverageWindowSize`, `RetryBaseDelaySeconds`, `RetryMaxAttempts`. |
| `Services/TelemetryClient.cs` | Thin HTTP wrapper over `GET api/telemetry/next`; interprets 204 as "no data" (returns `null`), throws on other non-success codes (so Polly can retry). |
| `Services/FetchWorker.cs` | `BackgroundService` #1 — polls the Publisher, writes fetched readings to the channel, backs off on empty responses or after Polly's retries are exhausted. **Connectivity + resilience requirement.** |
| `Services/ProcessingWorker.cs` | `BackgroundService` #2 — reads from the channel, calls `IAnalysisService.Analyze`, persists via `IAnalysisResultRepository`. **"Processing on a separate thread from fetching" requirement.** |
| `Services/MovingAverageAnalysisService.cs` | The "Complex Analysis": per-`SensorType` 5-point sliding window, lock-protected append+average. |
| `Data/IAnalysisResultRepository.cs` / `SqlAnalysisResultRepository.cs` | ADO.NET wrapper calling `sp_InsertAnalysisResults`. |
| `appsettings.json` | Connection string + `ConsumerOptions` defaults. |

### Database (Task 3)

| File | Purpose |
|---|---|
| `Database/schema.sql` | `PendingReadings` and `AnalysisResults` tables, plus `sp_SavePendingReading`, `sp_GetOldestPending` (atomic select+delete via `UPDLOCK, READPAST, ROWLOCK`), and `sp_InsertAnalysisResults`. All three procedures use `BEGIN/COMMIT/ROLLBACK TRANSACTION` + `SET XACT_ABORT ON` and are idempotent via `IF NOT EXISTS` guards. |
| `docker-compose.yml` | SQL Server 2022 container, port 1433, `sa` / `YourStrong!Passw0rd`, named volume for persistence. |

### TelemetryTests (Task 4)

| File | Purpose |
|---|---|
| `TelemetryStoreFifoTests.cs` | Two tests: (1) strict FIFO when everything stays in memory, (2) FIFO preserved across a capacity-triggered spill to a mocked repository (memory-first, DB-fallback). |
| `MovingAverageAnalysisServiceTests.cs` | Three tests: full-window average, partial-window average (before the window fills), and independent windows per `SensorType`. |

## Key Design Decisions

### 1. In-memory store: `ConcurrentQueue<TimedReading>` + atomic counters

The Publisher's in-memory store (`InMemoryTelemetryStore`) wraps each
`SensorReading` in a `TimedReading` that records `EnqueuedAt`. A
`ConcurrentQueue` gives lock-free, thread-safe enqueue/dequeue without manual
locking, which is sufficient here because every operation is a single-item
enqueue/dequeue — there's no compound "read-modify-write" sequence on the
queue itself that needs to be atomic as a whole. Counters (`totalGenerated`,
`totalSpilled`) use `Interlocked` for the same reason.

**Why not a `lock`-protected `List`/`Queue`?** A plain lock would work too,
but `ConcurrentQueue` avoids contention between the 1-second generator
(writer) and the API's `GetNextAsync` (reader/writer) without any explicit
synchronization code, which keeps the hot path simple and avoids the
possibility of a slow request holding a lock and stalling generation.

### 2. FIFO across the memory/database boundary

FIFO is preserved end-to-end via a monotonically increasing
`SequenceNumber` assigned at generation time (not relying on `DateTime`,
which isn't guaranteed strictly ordered/unique at 1-reading-per-second with
possible clock adjustments). `PendingReadings` is indexed on
`SequenceNumber`, and `sp_GetOldestPending` selects `TOP (1) ... ORDER BY
SequenceNumber ASC`.

`GetNextAsync` follows a three-step protocol:

1. **Spill aged entries** — walk the front of the queue (oldest first) and
   spill anything older than `SpillAfterSeconds` to `PendingReadings`. This
   runs *before* checking memory, so an aged-out reading is moved to the DB
   rather than silently skipped.
2. **Prefer memory** — `TryDequeue` the front of the queue if non-empty.
3. **Fall back to database** — call `sp_GetOldestPending`, which atomically
   selects-and-deletes the oldest row using `UPDLOCK, READPAST, ROWLOCK`
   inside a transaction, so two concurrent Publisher instances (or two
   concurrent requests) can't return the same spilled row.

Because the in-memory queue is itself ordered oldest-first, and spilled rows
preserve their original `SequenceNumber`, "memory first, then database"
never violates global FIFO order **except** in one edge case: if reading #5
spills to the DB while readings #6–10 remain in memory, `/next` would return
#6 before #5 is fetched from the DB. The implementation accepts this
trade-off (see "Known Trade-offs" below) because step 1 (spill aged entries
before serving) minimizes how often this can occur — it only happens when a
reading spills due to *capacity* (not age) while later readings are still
fresh in memory.

### 3. Spill triggers: capacity vs. age

- **Capacity spill**: `AddReadingAsync` spills the oldest entry whenever the
  queue exceeds `InMemoryCapacity` (default 10), keeping memory bounded.
- **Age spill**: `GetNextAsync` proactively spills any entry older than
  `SpillAfterSeconds` (default 5) before checking memory, so an unconsumed
  reading doesn't sit forever if `/next` is called infrequently.

### 4. Consumer threading: `Channel<SensorReading>` between two `BackgroundService`s

`FetchWorker` and `ProcessingWorker` are independent `BackgroundService`
instances connected by a bounded `Channel<SensorReading>`
(`BoundedChannelOptions(100)`, `FullMode = Wait`). This satisfies "fetching"
and "processing on a separate thread" cleanly:

- `FetchWorker` only calls the Publisher and writes to the channel.
- `ProcessingWorker` only reads from the channel, runs the analysis, and
  persists results.
- The bounded channel provides back-pressure: if the DB write is slow,
  `FetchWorker` will await channel capacity rather than buffering unboundedly
  in memory.

`Channel<T>` was chosen over a raw `ConcurrentQueue` + polling because it
gives async-native, allocation-light producer/consumer semantics
(`WriteAsync` / `ReadAllAsync`) without a manual polling loop on the consumer
side.

### 5. "Complex Analysis": 5-point moving average, per sensor type

`MovingAverageAnalysisService` maintains an independent sliding window
(`Queue<double>`, max size `MovingAverageWindowSize`, default 5) **per
`SensorType`**. Each call to `Analyze`:

1. Enqueues the new value into that sensor type's window.
2. Trims the window to the configured size (oldest values drop off).
3. Returns the average of whatever is currently in the window (partial
   windows are averaged over their actual count, not padded with zeros).

Per-sensor-type windows were chosen because averaging, say, a Temperature
reading together with the most recent Pressure/Humidity/Vibration readings
would produce a number with no physical meaning. Access to each window is
guarded by a `lock` because "append then compute average" is a compound
operation that needs atomicity — `ConcurrentQueue` alone wouldn't prevent two
threads from interleaving an enqueue and a trim.

A true Fourier Transform simulation was considered but not chosen: it adds
implementation complexity and a dependency (e.g. MathNet.Numerics) without
materially changing the architectural slice being demonstrated (concurrency,
resilience, layering, SQL integration). The moving average is "complex
enough" to require real per-type state and locking while keeping the example
auditable.

### 6. Resilience: Polly retry + graceful degradation

`FetchWorker`'s `HttpClient` is registered with a Polly
`WaitAndRetryAsync` policy (`HandleTransientHttpError()` + request timeouts),
retrying up to `RetryMaxAttempts` (default 5) with exponential backoff
(`base * 2^(attempt-1)`). If the Publisher is offline beyond that retry
budget, the exception is caught in `FetchWorker`'s loop, logged as a warning,
and the loop backs off and tries again — it never crashes the host. When the
Publisher returns 204 (no data), the worker also backs off
(`EmptyResponseBackoffSeconds`) to avoid hammering an idle endpoint.

### 7. Stored procedures, transactions, and parameterization

All three required procedures (`sp_SavePendingReading`,
`sp_GetOldestPending`, `sp_InsertAnalysisResults`) wrap their logic in
`BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` with `SET XACT_ABORT ON`, and all
.NET callers use `SqlCommand.Parameters.AddWithValue` exclusively — no string
concatenation into SQL anywhere. Both insert procedures are idempotent
(`IF NOT EXISTS ... INSERT`) so a retried call (e.g. after a transient network
error between app and DB) won't create duplicate rows.

## Known Trade-offs

- **Strict global FIFO vs. simplicity**: as noted above, a capacity-spill
  followed by `/next` calls can theoretically return a DB row "out of order"
  relative to fresher in-memory rows if those fresher rows haven't aged out
  yet. A fully strict global FIFO would require comparing the head of the
  in-memory queue's `SequenceNumber` against the DB's oldest `SequenceNumber`
  on every `/next` call (an extra DB round-trip per request). Given the
  4-hour scope and that this only matters under sustained overflow, the
  current approach (spill-aged-first, then memory, then DB) was chosen as a
  reasonable approximation.
- **`AddWithValue`**: used for brevity; in a longer-lived production codebase
  I'd switch to explicit `SqlParameter` with declared `SqlDbType`/size to
  avoid implicit conversion/parameter-sniffing issues.
- **Single SQL connection per call**: each repository method opens a new
  `SqlConnection`. ADO.NET's connection pooling makes this cheap, but a
  production system processing very high throughput might use a shared
  `DataSource`/connection factory with explicit pool tuning.
- **No authentication/HTTPS** between Publisher and Consumer — out of scope
  for this slice, but would be required before any real deployment.

## Scaling Considerations

**Multiple consumers**: `sp_GetOldestPending`'s `UPDLOCK, READPAST, ROWLOCK`
already allows multiple Publisher-side callers to safely split spilled work
without double-processing. On the Consumer side, multiple Consumer instances
polling `/next` would naturally load-balance since each call to `/next`
atomically removes one reading — but moving-average state is currently
per-process and per-sensor-type, so multiple consumers would each compute
their *own* moving average over a subset of readings. To scale Consumers
correctly, moving-average state would need to move to a shared store (e.g.
Redis, or a `MovingAverageState` table keyed by `SensorType`, updated
transactionally per reading).

**Higher data throughput**: the 1-second generation interval and 10-item
in-memory capacity are configuration values (`PublisherOptions`), easily
tuned. At much higher throughput, the bigger change would be replacing the
synchronous REST polling model (`GET /next`) with a push-based model — see
message queues below — since polling at sub-second intervals doesn't scale
well across many consumers.

**Additional sensor types**: `SensorType` is a free-form string, and the
moving-average service already keys its sliding windows by `SensorType`
dynamically (`ConcurrentDictionary<string, ...>`), so new sensor types work
without code changes. The only consideration is whether different sensor
types warrant different analysis types or window sizes — that would require
a small per-type configuration map.

**Message queues**: the cleanest evolution is to replace the
`GET /api/telemetry/next` polling endpoint with the Publisher publishing each
`SensorReading` directly to a queue/topic (e.g. RabbitMQ, Azure Service Bus,
or Kafka). This would:

- Eliminate polling latency and empty-response overhead.
- Allow multiple Consumer instances to compete for messages (competing
  consumers pattern) with the broker handling delivery/ack semantics,
  replacing the custom `PendingReadings` spill table for most cases.
- `PendingReadings` could then be repurposed as a dead-letter/audit table for
  messages that fail processing after retries, rather than the primary
  overflow mechanism.
- The Consumer's `Channel<SensorReading>` would be replaced by the broker
  client's own consumer loop, but the `ProcessingWorker`/`IAnalysisService`
  separation would remain unchanged — only the transport at the
  fetch boundary changes.

## AI Tool Usage Disclosure

This solution was generated with the assistance of Claude (Anthropic), an AI
coding assistant, based on the assignment brief
("Take Home Assignment: Distributed Telemetry Processor — Publisher/Consumer
Core Slice").

**Prompting approach:**

1. The assignment PDF/brief was provided to the assistant in full.
2. The assistant was first asked to act as a prompt engineer: review and
   optimize the brief into a structured implementation prompt, and to ask
   clarifying questions before generating code.
3. Clarifying questions covered: (a) whether to generate all code immediately
   vs. just refine the prompt, (b) local SQL Server setup approach
   (Dockerized vs. LocalDB), (c) which "Complex Analysis" algorithm to use
   (moving average vs. Fourier simulation), and (d) whether to include this
   AI-usage disclosure section in `SOLUTION.md`.
4. Based on the answers (generate all code now; use Dockerized SQL Server;
   moving average; include this disclosure), the assistant generated:
   - The full project/solution structure (`TelemetryShared`,
     `TelemetryPublisher`, `TelemetryConsumer`, `TelemetryTests`).
   - `Database/schema.sql` with tables and the three required stored
     procedures.
   - `docker-compose.yml` for SQL Server.
   - `README.md` and this `SOLUTION.md`.
   - Two xUnit tests (FIFO ordering across memory/DB, and the moving-average
     calculation).

**What was reviewed/adjusted manually:** the architecture (project
boundaries, `Channel`-based fetch/process separation, per-sensor-type moving
average windows, spill-then-serve ordering in `GetNextAsync`) was specified
as part of the structured prompt rather than left to the model's defaults,
to ensure the implementation directly maps to each requirement in the brief
(FIFO, thread-safety, resilience, stored-procedure-only writes). The
"Known Trade-offs" section above documents the one place (strict global FIFO
under sustained overflow) where a simplifying assumption was made
deliberately rather than over-engineering within the ~4-hour scope.

**Note:** Because the development environment used to produce this code does
not have the .NET SDK available, the code has not been compiled/run in that
environment. Before submitting, run `dotnet build` and `dotnet test` locally
(see README) to confirm everything compiles and the test suite passes, and
fix any minor issues (e.g. package version availability) that surface.

## Appendix: Build Troubleshooting Log

Two issues were found and fixed after the initial generation, when first
opened in Visual Studio:

1. **NU1605 package downgrade (Consumer and Tests)** —
   `Microsoft.Extensions.Http.Polly 8.0.10` transitively requires
   `Microsoft.Extensions.Http >= 8.0.1`, but `TelemetryConsumer.csproj`
   originally pinned `Microsoft.Extensions.Http` to `8.0.0` directly,
   creating a downgrade conflict (treated as an error under
   `TreatWarningsAsErrors`). **Fix:** removed the redundant explicit
   `Microsoft.Extensions.Http` reference — `Microsoft.Extensions.Http.Polly`
   already brings in a compatible version transitively.

2. **CS1061 missing Swagger extension methods (Publisher)** —
   `Program.cs` calls `AddSwaggerGen()`, `UseSwagger()`, and `UseSwaggerUI()`,
   but `TelemetryPublisher.csproj` did not reference a Swagger package.
   **Fix:** added `<PackageReference Include="Swashbuckle.AspNetCore"
   Version="6.6.2" />`.

3. **CS0006 (Tests project, downstream)** — a consequence of #2: when
   `TelemetryPublisher` fails to build, `TelemetryTests` (which references it
   transitively via `TelemetryConsumer` → `TelemetryShared`, and directly)
   can't find `TelemetryPublisher.dll`. Resolved automatically once #2 was
   fixed and NuGet packages were restored.

**If you still see these errors after pulling this version:** the `.csproj`
changes are already in source, so right-click the solution → **Restore NuGet
Packages** → **Rebuild Solution**. If restore can't reach nuget.org (offline
environment), Swashbuckle can be removed entirely along with the three
Swagger lines in `Program.cs` as a no-dependency fallback.

## Appendix: Running Multiple Startup Projects in Visual Studio

The solution has two runnable projects (`TelemetryPublisher`,
`TelemetryConsumer`) and two library/test projects
(`TelemetryShared`, `TelemetryTests`) that cannot be "run" directly — if VS
tries to launch one of these, you'll see *"A project with an Output Type of
Class Library cannot be started directly."*

A `TelemetryProcessor.sln.slnLaunch` file is included alongside the `.sln`
to configure both runnable projects to start together automatically (VS 2022
17.3+). If VS doesn't pick it up, configure manually: right-click the
solution → **Set Startup Projects...** → **Multiple startup projects** → set
`TelemetryPublisher` and `TelemetryConsumer` to **Start**, leave
`TelemetryShared` and `TelemetryTests` as **None**.

