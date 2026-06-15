namespace TelemetryShared.Models;

/// <summary>
/// Represents the output of the Consumer's complex analysis for a given reading.
/// </summary>
public class AnalysisResult
{
    /// <summary>Stable unique identifier, e.g. "ar_5001".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The SensorReading.Id this result was computed from.</summary>
    public string SensorReadingId { get; set; } = string.Empty;

    /// <summary>The type of analysis performed, e.g. "MovingAverage".</summary>
    public string AnalysisType { get; set; } = string.Empty;

    /// <summary>The computed analysis result value.</summary>
    public double Result { get; set; }

    /// <summary>UTC timestamp when the analysis was completed.</summary>
    public DateTime ProcessedAt { get; set; }

    /// <summary>Correlation ID linking this result back to the fetch/process/persist pipeline for log tracing.</summary>
    public string CorrelationId { get; set; } = string.Empty;
}
