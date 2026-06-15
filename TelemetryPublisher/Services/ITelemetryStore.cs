using TelemetryShared.Models;

namespace TelemetryPublisher.Services;

/// <summary>
/// Wraps a SensorReading with the time it entered the in-memory store,
/// used to determine spill eligibility (age-based eviction).
/// </summary>
public class TimedReading
{
    public required SensorReading Reading { get; init; }
    public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe in-memory store for the most recent sensor readings, with
/// spill-to-database support when capacity or age limits are exceeded.
/// </summary>
public interface ITelemetryStore
{
    /// <summary>Adds a new reading, spilling the oldest entry to the DB if over capacity.</summary>
    Task AddReadingAsync(SensorReading reading, CancellationToken ct = default);

    /// <summary>
    /// Returns and removes the oldest unconsumed reading, preferring memory over the
    /// database, while also spilling any in-memory entries that have aged out.
    /// </summary>
    Task<SensorReading?> GetNextAsync(CancellationToken ct = default);

    /// <summary>Returns current statistics: queue depth, total generated, total spilled.</summary>
    TelemetryStats GetStats();
}

/// <summary>Snapshot of publisher statistics for the /stats endpoint.</summary>
public record TelemetryStats(int QueueDepth, long TotalGenerated, long TotalSpilled);
