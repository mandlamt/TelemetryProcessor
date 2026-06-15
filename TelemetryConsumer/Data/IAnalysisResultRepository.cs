using TelemetryShared.Models;

namespace TelemetryConsumer.Data;

/// <summary>
/// Data-access abstraction for persisting analysis results via
/// sp_InsertAnalysisResults.
/// </summary>
public interface IAnalysisResultRepository
{
    Task SaveAsync(AnalysisResult result, CancellationToken ct = default);
}
