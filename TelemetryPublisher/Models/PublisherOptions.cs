namespace TelemetryPublisher.Models;

/// <summary>
/// Configuration options bound from the "PublisherOptions" section of appsettings.json.
/// </summary>
public class PublisherOptions
{
    public const string SectionName = "PublisherOptions";

    /// <summary>How often a new SensorReading is generated.</summary>
    public int GenerationIntervalSeconds { get; set; } = 1;

    /// <summary>Maximum number of readings held in the in-memory queue before spilling.</summary>
    public int InMemoryCapacity { get; set; } = 10;

    /// <summary>Maximum age (seconds) a reading may sit in memory before being spilled to the database.</summary>
    public int SpillAfterSeconds { get; set; } = 5;
}
