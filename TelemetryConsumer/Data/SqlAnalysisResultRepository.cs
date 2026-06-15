using Microsoft.Data.SqlClient;
using TelemetryShared.Models;

namespace TelemetryConsumer.Data;

/// <summary>
/// SQL Server implementation of IAnalysisResultRepository. Writes go through
/// sp_InsertAnalysisResults using parameterized inputs only.
/// </summary>
public class SqlAnalysisResultRepository : IAnalysisResultRepository
{
    private readonly string _connectionString;

    public SqlAnalysisResultRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("TelemetryDb")
            ?? throw new InvalidOperationException("Connection string 'TelemetryDb' is not configured.");
    }

    public async Task SaveAsync(AnalysisResult result, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand("sp_InsertAnalysisResults", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@Id", result.Id);
        command.Parameters.AddWithValue("@SensorReadingId", result.SensorReadingId);
        command.Parameters.AddWithValue("@AnalysisType", result.AnalysisType);
        command.Parameters.AddWithValue("@Result", result.Result);
        command.Parameters.AddWithValue("@ProcessedAt", result.ProcessedAt);
        command.Parameters.AddWithValue("@CorrelationId", result.CorrelationId);

        await command.ExecuteNonQueryAsync(ct);
    }
}
