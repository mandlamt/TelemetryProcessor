using Microsoft.Data.SqlClient;
using TelemetryShared.Models;

namespace TelemetryPublisher.Data;

/// <summary>
/// SQL Server implementation of IPendingReadingRepository. All access goes
/// through stored procedures with parameterized inputs (no inline SQL) to
/// satisfy the injection-prevention requirement.
/// </summary>
public class SqlPendingReadingRepository : IPendingReadingRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlPendingReadingRepository> _logger;

    public SqlPendingReadingRepository(IConfiguration configuration, ILogger<SqlPendingReadingRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("TelemetryDb")
            ?? throw new InvalidOperationException("Connection string 'TelemetryDb' is not configured.");
        _logger = logger;
    }

    public async Task SavePendingReadingAsync(SensorReading reading, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand("sp_SavePendingReading", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@Id", reading.Id);
        command.Parameters.AddWithValue("@Timestamp", reading.Timestamp);
        command.Parameters.AddWithValue("@Value", reading.Value);
        command.Parameters.AddWithValue("@SensorType", reading.SensorType);
        command.Parameters.AddWithValue("@SequenceNumber", reading.SequenceNumber);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SensorReading?> GetAndRemoveOldestPendingAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand("sp_GetOldestPending", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SensorReading
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Timestamp = reader.GetDateTime(reader.GetOrdinal("Timestamp")),
            Value = reader.GetDouble(reader.GetOrdinal("Value")),
            SensorType = reader.GetString(reader.GetOrdinal("SensorType")),
            SequenceNumber = reader.GetInt64(reader.GetOrdinal("SequenceNumber"))
        };
    }
}
