namespace TelemetryShared.Models;

/// <summary>
/// Represents a single sensor reading produced by the Publisher.
/// </summary>
public class SensorReading
{
    /// <summary>Stable unique identifier, e.g. "sr_10001".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>UTC timestamp in ISO 8601 format.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Measured value.</summary>
    public double Value { get; set; }

    /// <summary>Sensor category, e.g. Temperature, Pressure, Humidity, Vibration.</summary>
    public string SensorType { get; set; } = string.Empty;

    /// <summary>
    /// Monotonically increasing sequence number assigned at generation time.
    /// Used to guarantee FIFO ordering across the in-memory/database boundary,
    /// since DateTime alone is not guaranteed to be strictly unique/ordered
    /// at sub-millisecond generation rates and across process restarts.
    /// </summary>
    public long SequenceNumber { get; set; }
}
