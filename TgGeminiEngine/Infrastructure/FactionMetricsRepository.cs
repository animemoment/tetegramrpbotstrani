using Microsoft.Data.Sqlite;
using System.Text.Json;
using TgGeminiEngine.Domain;

namespace TgGeminiEngine.Infrastructure;

// Хранение структурированных метрик государства по кварталам
public class FactionMetricsRepository
{
    private readonly string _connectionString;

    public FactionMetricsRepository(string connectionString) => _connectionString = connectionString;

    public async Task SaveQuarterMetricsAsync(long userId, int year, int quarter, FactionMetrics? metrics, string summary)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO faction_metrics (user_id, year, quarter, metrics_json, summary, created_at)
            VALUES ($uid, $year, $quarter, $json, $summary, $created)
            ON CONFLICT(user_id, year, quarter) DO UPDATE SET
                metrics_json = excluded.metrics_json,
                summary = excluded.summary;";

        command.Parameters.AddWithValue("$uid", userId);
        command.Parameters.AddWithValue("$year", year);
        command.Parameters.AddWithValue("$quarter", quarter);
        command.Parameters.AddWithValue("$json", metrics is null ? (object)DBNull.Value : JsonSerializer.Serialize(metrics));
        command.Parameters.AddWithValue("$summary", (object?)summary ?? string.Empty);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<QuarterMetricsRecord>> GetYearHistoryAsync(long userId, int year)
    {
        var list = new List<QuarterMetricsRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT year, quarter, metrics_json, summary
            FROM faction_metrics
            WHERE user_id = $uid AND year = $year
            ORDER BY quarter ASC;";
        command.Parameters.AddWithValue("$uid", userId);
        command.Parameters.AddWithValue("$year", year);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string? json = reader.IsDBNull(2) ? null : reader.GetString(2);
            list.Add(new QuarterMetricsRecord(
                reader.GetInt32(0),
                reader.GetInt32(1),
                json is null ? null : JsonSerializer.Deserialize<FactionMetrics>(json),
                reader.GetString(3)
            ));
        }
        return list;
    }

    public async Task<List<YearMetricsRecord>> GetAllMetricsForYearAsync(int year)
    {
        var list = new List<YearMetricsRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT user_id, year, quarter, metrics_json, summary
            FROM faction_metrics
            WHERE year = $year
            ORDER BY user_id, quarter ASC;";
        command.Parameters.AddWithValue("$year", year);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string? json = reader.IsDBNull(3) ? null : reader.GetString(3);
            list.Add(new YearMetricsRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                json is null ? null : JsonSerializer.Deserialize<FactionMetrics>(json),
                reader.GetString(4)
            ));
        }
        return list;
    }
}