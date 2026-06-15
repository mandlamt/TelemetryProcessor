using System.Net;
using System.Net.Http.Json;
using TelemetryShared.Models;

namespace TelemetryConsumer.Services;

/// <summary>
/// HTTP client wrapper for the Publisher's telemetry endpoint.
/// Retry/backoff for transient failures is handled by the Polly policy
/// configured on the underlying HttpClient (see Program.cs); this class
/// focuses on request shaping and response interpretation.
/// </summary>
public interface ITelemetryClient
{
    /// <summary>
    /// Fetches the next available reading. Returns null if the Publisher
    /// responds with "no data available" (204 No Content).
    /// </summary>
    Task<SensorReading?> GetNextReadingAsync(CancellationToken ct = default);
}

public class TelemetryClient : ITelemetryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelemetryClient> _logger;

    public TelemetryClient(HttpClient httpClient, ILogger<TelemetryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SensorReading?> GetNextReadingAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync("api/telemetry/next", ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var reading = await response.Content.ReadFromJsonAsync<SensorReading>(cancellationToken: ct);
        return reading;
    }
}
