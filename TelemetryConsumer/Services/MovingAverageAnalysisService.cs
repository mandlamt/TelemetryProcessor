using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TelemetryConsumer.Models;
using TelemetryShared.Models;

namespace TelemetryConsumer.Services;

/// <summary>
/// Performs the "Complex Analysis" step: a per-SensorType 5-point moving average.
///
/// Design:
/// - Each SensorType maintains its own fixed-size sliding window (a Queue
///   wrapped for thread safety), so analysis is contextually meaningful
///   (e.g. Temperature readings are averaged against recent Temperature
///   readings only, not mixed with Pressure/Humidity/Vibration).
/// - Until a window has at least one value, the moving average equals the
///   current reading (partial windows are averaged over however many values
///   exist so far rather than waiting for the window to fill).
/// - Access to each per-type window is protected by a lock to keep the
///   "append + compute average" sequence atomic, since this is a
///   compound operation that ConcurrentQueue alone cannot make atomic.
/// </summary>
public interface IAnalysisService
{
    /// <summary>
    /// Computes the updated moving average for the given reading's sensor type,
    /// after appending the reading's value to that type's sliding window.
    /// </summary>
    AnalysisResult Analyze(SensorReading reading, string correlationId);
}

public class MovingAverageAnalysisService : IAnalysisService
{
    private readonly int _windowSize;
    private readonly ConcurrentDictionary<string, (Queue<double> Window, object Lock)> _windows = new();
    private long _resultSequence;

    public MovingAverageAnalysisService(IOptions<ConsumerOptions> options)
    {
        _windowSize = Math.Max(1, options.Value.MovingAverageWindowSize);
    }

    public AnalysisResult Analyze(SensorReading reading, string correlationId)
    {
        var entry = _windows.GetOrAdd(reading.SensorType, _ => (new Queue<double>(), new object()));

        double average;
        lock (entry.Lock)
        {
            entry.Window.Enqueue(reading.Value);

            while (entry.Window.Count > _windowSize)
            {
                entry.Window.Dequeue();
            }

            average = entry.Window.Average();
        }

        var seq = Interlocked.Increment(ref _resultSequence);

        return new AnalysisResult
        {
            Id = $"ar_{seq:D6}",
            SensorReadingId = reading.Id,
            AnalysisType = "MovingAverage",
            Result = Math.Round(average, 3),
            ProcessedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }
}
