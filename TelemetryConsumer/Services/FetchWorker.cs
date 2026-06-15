using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TelemetryConsumer.Models;
using TelemetryShared.Models;

namespace TelemetryConsumer.Services;

/// <summary>
/// Background service responsible solely for fetching readings from the
/// Publisher and handing them off to the processing pipeline via a Channel.
///
/// Threading model:
/// - This service runs on its own logical thread (BackgroundService's
///   ExecuteAsync runs on a thread-pool thread independent of the host's
///   main thread, keeping the Worker Service responsive).
/// - Fetched readings are written to a bounded Channel<SensorReading>, which
///   ProcessingWorker reads from on a *different* thread. This satisfies the
///   "processing must happen on a separate thread from fetching" requirement
///   without needing manual locks: Channel<T> is a thread-safe, async-friendly
///   producer/consumer queue.
/// - Resilience: HTTP retry/backoff against the Publisher being offline is
///   configured on the HttpClient via a Polly policy (registered in
///   Program.cs). This service additionally catches any residual exceptions
///   (e.g. after retries are exhausted) so a transient outage never crashes
///   the host -- it logs and waits before trying again.
/// </summary>
public class FetchWorker : BackgroundService
{
    private readonly ITelemetryClient _client;
    private readonly ChannelWriter<SensorReading> _writer;
    private readonly ConsumerOptions _options;
    private readonly ILogger<FetchWorker> _logger;

    public FetchWorker(
        ITelemetryClient client,
        Channel<SensorReading> channel,
        IOptions<ConsumerOptions> options,
        ILogger<FetchWorker> logger)
    {
        _client = client;
        _writer = channel.Writer;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FetchWorker starting. Polling {Url}", _options.PublisherBaseUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            TimeSpan delay;

            try
            {
                var reading = await _client.GetNextReadingAsync(stoppingToken);

                if (reading is null)
                {
                    _logger.LogDebug("[{CorrelationId}] No data available from Publisher.", correlationId);
                    delay = TimeSpan.FromSeconds(_options.EmptyResponseBackoffSeconds);
                }
                else
                {
                    _logger.LogInformation(
                        "[{CorrelationId}] Fetched reading {ReadingId} ({SensorType}={Value})",
                        correlationId, reading.Id, reading.SensorType, reading.Value);

                    await _writer.WriteAsync(reading, stoppingToken);
                    delay = TimeSpan.FromSeconds(_options.PollingIntervalSeconds);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Polly already retried with backoff at the HttpClient level.
                // If we still land here, the Publisher is offline beyond our
                // retry budget -- log and back off without crashing.
                _logger.LogWarning(ex, "[{CorrelationId}] Publisher unreachable after retries. Backing off.", correlationId);
                delay = TimeSpan.FromSeconds(Math.Max(_options.RetryBaseDelaySeconds, _options.EmptyResponseBackoffSeconds));
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _writer.TryComplete();
        _logger.LogInformation("FetchWorker stopping.");
    }
}
