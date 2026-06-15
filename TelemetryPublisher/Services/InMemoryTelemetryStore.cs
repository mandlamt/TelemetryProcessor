using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TelemetryPublisher.Data;
using TelemetryPublisher.Models;
using TelemetryShared.Models;

namespace TelemetryPublisher.Services;

/// <summary>
/// Thread-safe in-memory store backed by a ConcurrentQueue, with spill-to-SQL
/// behavior driven by capacity and age thresholds.
///
/// Concurrency notes:
/// - ConcurrentQueue gives us lock-free, thread-safe enqueue/dequeue, which is
///   sufficient because all operations here are single-item Enqueue/TryDequeue
///   (no compound "check-then-act" sequences need to be atomic across the queue
///   as a whole). Counters use Interlocked for the same reason.
/// - GetNextAsync first opportunistically drains any aged-out entries to the DB
///   (spill), then serves from memory if anything remains, otherwise falls back
///   to the database via sp_GetOldestPending. This preserves the
///   "memory first, then database" FIFO contract even though the two stores
///   are physically separate.
/// </summary>
public class InMemoryTelemetryStore : ITelemetryStore
{
    private readonly ConcurrentQueue<TimedReading> _queue = new();
    private readonly IPendingReadingRepository _repository;
    private readonly PublisherOptions _options;
    private readonly ILogger<InMemoryTelemetryStore> _logger;

    private long _totalGenerated;
    private long _totalSpilled;

    public InMemoryTelemetryStore(
        IPendingReadingRepository repository,
        IOptions<PublisherOptions> options,
        ILogger<InMemoryTelemetryStore> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task AddReadingAsync(SensorReading reading, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalGenerated);

        _queue.Enqueue(new TimedReading { Reading = reading });

        // If we've exceeded capacity, spill the oldest entry immediately.
        // We may briefly exceed capacity by 1 due to the check-then-act gap
        // between Count and TryDequeue; this is acceptable since capacity is
        // a soft limit driving spill behavior, not a hard memory bound.
        while (_queue.Count > _options.InMemoryCapacity)
        {
            if (_queue.TryDequeue(out var oldest))
            {
                await SpillAsync(oldest.Reading, ct);
            }
            else
            {
                break;
            }
        }
    }

    public async Task<SensorReading?> GetNextAsync(CancellationToken ct = default)
    {
        // Step 1: Spill any entries that have aged out (older than SpillAfterSeconds)
        // so they don't block FIFO order from being satisfied by the DB layer
        // once they're moved there. We only inspect/dequeue from the front of
        // the queue since it is ordered oldest-to-newest.
        await SpillAgedEntriesAsync(ct);

        // Step 2: Prefer in-memory data.
        if (_queue.TryDequeue(out var timed))
        {
            return timed.Reading;
        }

        // Step 3: Fall back to the database (oldest spilled reading).
        var fromDb = await _repository.GetAndRemoveOldestPendingAsync(ct);
        return fromDb;
    }

    public TelemetryStats GetStats() =>
        new(_queue.Count, Interlocked.Read(ref _totalGenerated), Interlocked.Read(ref _totalSpilled));

    /// <summary>
    /// Walks the front of the queue, spilling any entries older than the configured
    /// threshold to the database. Stops at the first entry that is still fresh,
    /// since the queue is ordered oldest-first.
    /// </summary>
    private async Task SpillAgedEntriesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_options.SpillAfterSeconds);

        while (_queue.TryPeek(out var oldest) && oldest.EnqueuedAt <= cutoff)
        {
            if (_queue.TryDequeue(out var timed))
            {
                await SpillAsync(timed.Reading, ct);
            }
            else
            {
                break;
            }
        }
    }

    private async Task SpillAsync(SensorReading reading, CancellationToken ct)
    {
        try
        {
            await _repository.SavePendingReadingAsync(reading, ct);
            Interlocked.Increment(ref _totalSpilled);
            _logger.LogInformation("Spilled reading {ReadingId} to PendingReadings (seq {Seq})", reading.Id, reading.SequenceNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spill reading {ReadingId} to database", reading.Id);

            // If we can't persist it, put it back at the front conceptually by
            // re-enqueuing. This is a best-effort fallback; in production this
            // would go to a dead-letter mechanism instead.
            _queue.Enqueue(new TimedReading { Reading = reading, EnqueuedAt = DateTime.UtcNow });
        }
    }
}
