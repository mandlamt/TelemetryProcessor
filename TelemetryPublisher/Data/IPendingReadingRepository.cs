using TelemetryShared.Models;

namespace TelemetryPublisher.Data;

/// <summary>
/// Data-access abstraction over the PendingReadings table, implemented via
/// stored procedure calls only (sp_SavePendingReading, sp_GetOldestPending).
/// </summary>
public interface IPendingReadingRepository
{
    /// <summary>Persists a spilled reading via sp_SavePendingReading.</summary>
    Task SavePendingReadingAsync(SensorReading reading, CancellationToken ct = default);

    /// <summary>
    /// Atomically retrieves and removes the oldest pending reading via
    /// sp_GetOldestPending (the proc performs SELECT + DELETE in one transaction).
    /// Returns null if no pending readings exist.
    /// </summary>
    Task<SensorReading?> GetAndRemoveOldestPendingAsync(CancellationToken ct = default);
}
