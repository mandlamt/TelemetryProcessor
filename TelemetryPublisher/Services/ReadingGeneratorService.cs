using Microsoft.Extensions.Options;
using TelemetryPublisher.Models;
using TelemetryShared.Models;

namespace TelemetryPublisher.Services;

/// <summary>
/// Background service that generates a new SensorReading on a fixed interval
/// and adds it to the in-memory store. Runs for the lifetime of the application.
/// </summary>
public class ReadingGeneratorService : BackgroundService
{
    private static readonly string[] SensorTypes = { "Temperature", "Pressure", "Humidity", "Vibration" };

    private readonly ITelemetryStore _store;
    private readonly PublisherOptions _options;
    private readonly ILogger<ReadingGeneratorService> _logger;
    private readonly Random _random = new();
    private long _sequence;

    public ReadingGeneratorService(
        ITelemetryStore store,
        IOptions<PublisherOptions> options,
        ILogger<ReadingGeneratorService> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.GenerationIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var reading = GenerateReading();

            try
            {
                await _store.AddReadingAsync(reading, stoppingToken);
                _logger.LogInformation(
                    "Generated reading {Id} ({SensorType}={Value:F3}) seq={Seq}",
                    reading.Id, reading.SensorType, reading.Value, reading.SequenceNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add generated reading {Id} to the store", reading.Id);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Expected during shutdown.
            }
        }
    }

    private SensorReading GenerateReading()
    {
        var seq = Interlocked.Increment(ref _sequence);
        var sensorType = SensorTypes[_random.Next(SensorTypes.Length)];

        // Generate a plausible value range per sensor type.
        var value = sensorType switch
        {
            "Temperature" => 15 + _random.NextDouble() * 20,   // 15-35 C
            "Pressure" => 980 + _random.NextDouble() * 40,      // 980-1020 hPa
            "Humidity" => 30 + _random.NextDouble() * 50,       // 30-80 %
            "Vibration" => _random.NextDouble() * 5,            // 0-5 mm/s
            _ => _random.NextDouble()
        };

        return new SensorReading
        {
            Id = $"sr_{seq:D6}",
            Timestamp = DateTime.UtcNow,
            Value = Math.Round(value, 3),
            SensorType = sensorType,
            SequenceNumber = seq
        };
    }
}
