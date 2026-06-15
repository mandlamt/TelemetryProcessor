using System.Threading.Channels;
using TelemetryConsumer.Data;
using TelemetryShared.Models;

namespace TelemetryConsumer.Services;

/// <summary>
/// Background service that reads fetched readings from the channel, performs
/// the "Complex Analysis" (5-point moving average per sensor type), and
/// persists the result via sp_InsertAnalysisResults.
///
/// This runs as an independent BackgroundService -- a separate logical
/// thread/task from FetchWorker -- satisfying the requirement that analysis
/// happens on a thread separate from fetching. The Channel provides the
/// thread-safe handoff between the two.
/// </summary>
public class ProcessingWorker : BackgroundService
{
    private readonly ChannelReader<SensorReading> _reader;
    private readonly IAnalysisService _analysisService;
    private readonly IAnalysisResultRepository _repository;
    private readonly ILogger<ProcessingWorker> _logger;

    public ProcessingWorker(
        Channel<SensorReading> channel,
        IAnalysisService analysisService,
        IAnalysisResultRepository repository,
        ILogger<ProcessingWorker> logger)
    {
        _reader = channel.Reader;
        _analysisService = analysisService;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProcessingWorker starting.");

        try
        {
            await foreach (var reading in _reader.ReadAllAsync(stoppingToken))
            {
                var correlationId = Guid.NewGuid().ToString("N");
                await ProcessReadingAsync(reading, correlationId, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _logger.LogInformation("ProcessingWorker stopping.");
    }

    private async Task ProcessReadingAsync(SensorReading reading, string correlationId, CancellationToken ct)
    {
        try
        {
            var result = _analysisService.Analyze(reading, correlationId);

            _logger.LogInformation(
                "[{CorrelationId}] Analyzed reading {ReadingId}: {AnalysisType}={Result}",
                correlationId, reading.Id, result.AnalysisType, result.Result);

            await _repository.SaveAsync(result, ct);

            _logger.LogInformation(
                "[{CorrelationId}] Persisted analysis result {ResultId} for reading {ReadingId}",
                correlationId, result.Id, reading.Id);
        }
        catch (Exception ex)
        {
            // A failure to analyze/persist a single reading should not stop
            // the pipeline -- log and move on to the next item.
            _logger.LogError(ex, "[{CorrelationId}] Failed to process reading {ReadingId}", correlationId, reading.Id);
        }
    }
}
