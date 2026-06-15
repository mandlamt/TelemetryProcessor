namespace TelemetryConsumer.Models;

/// <summary>
/// Configuration options bound from the "ConsumerOptions" section of appsettings.json.
/// </summary>
public class ConsumerOptions
{
    public const string SectionName = "ConsumerOptions";

    /// <summary>Base URL of the Publisher's Web API.</summary>
    public string PublisherBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>How often to poll api/telemetry/next when data is available.</summary>
    public int PollingIntervalSeconds { get; set; } = 1;

    /// <summary>How long to wait before polling again after receiving an empty (204) response.</summary>
    public int EmptyResponseBackoffSeconds { get; set; } = 2;

    /// <summary>Window size (number of readings) for the moving-average analysis, per sensor type.</summary>
    public int MovingAverageWindowSize { get; set; } = 5;

    /// <summary>Base delay (seconds) for exponential backoff retry when the Publisher is unreachable.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>Maximum number of consecutive retry attempts before backoff is capped.</summary>
    public int RetryMaxAttempts { get; set; } = 5;
}
