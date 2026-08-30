using Microsoft.Data.Sqlite;
using TgGeminiEngine.Domain;

namespace TgGeminiEngine.Infrastructure;

public class ChannelPostRepository
{
    private readonly string _connectionString;

    public ChannelPostRepository(string connectionString) => _connectionString = connectionString;

    public async Task SavePostAsync(long channelId, int messageId, DateTime date, string content)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO channel_posts (channel_id, message_id, post_date, content, is_processed)
            VALUES ($cid, $mid, $date, $content, 0)
            ON CONFLICT(channel_id, message_id) DO UPDATE SET content = excluded.content, post_date = excluded.post_date;";
        
        command.Parameters.AddWithValue("$cid", channelId);
        command.Parameters.AddWithValue("$mid", messageId);
        command.Parameters.AddWithValue("$date", date.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$content", content);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<ChannelPostRecord>> GetUnprocessedPostsAsync(long channelId)
    {
        var list = new List<ChannelPostRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT channel_id, message_id, post_date, content, is_processed 
            FROM channel_posts 
            WHERE channel_id = $cid AND is_processed = 0 
            ORDER BY message_id ASC;";
        command.Parameters.AddWithValue("$cid", channelId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ChannelPostRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                DateTime.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4) == 1
            ));
        }
        return list;
    }

    public async Task MarkPostsAsProcessedAsync(long channelId, int upToMessageId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE channel_posts 
            SET is_processed = 1 
            WHERE channel_id = $cid AND message_id <= $mid;";
        command.Parameters.AddWithValue("$cid", channelId);
        command.Parameters.AddWithValue("$mid", upToMessageId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetLastKnownMessageIdAsync(long channelId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(message_id), 0) FROM channel_posts WHERE channel_id = $cid";
        command.Parameters.AddWithValue("$cid", channelId);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task RegisterKnownChannelAsync(
        long channelId, 
        string? title, 
        string? username, 
        long ownerId = 0, 
        string? ownerUsername = null, 
        bool isActive = true)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO known_channels (channel_id, title, username, owner_id, owner_username, is_active)
            VALUES ($cid, $title, $uname, $oid, $ouname, $active)
            ON CONFLICT(channel_id) DO UPDATE SET 
                title = COALESCE(excluded.title, known_channels.title),
                username = COALESCE(excluded.username, known_channels.username),
                owner_id = CASE WHEN excluded.owner_id != 0 THEN excluded.owner_id ELSE known_channels.owner_id END,
                owner_username = CASE WHEN excluded.owner_username != '' THEN excluded.owner_username ELSE known_channels.owner_username END,
                is_active = excluded.is_active;";
        
        command.Parameters.AddWithValue("$cid", channelId);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$uname", (object?)username ?? DBNull.Value);
        command.Parameters.AddWithValue("$oid", ownerId);
        command.Parameters.AddWithValue("$ouname", (object?)ownerUsername ?? string.Empty);
        command.Parameters.AddWithValue("$active", isActive ? 1 : 0);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateChannelOwnerAsync(long channelId, long ownerId, string ownerUsername)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE known_channels 
            SET owner_id = $oid, owner_username = $ouname 
            WHERE channel_id = $cid;";
        command.Parameters.AddWithValue("$cid", channelId);
        command.Parameters.AddWithValue("$oid", ownerId);
        command.Parameters.AddWithValue("$ouname", ownerUsername ?? string.Empty);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<KnownChannelRecord>> GetAllKnownChannelsAsync()
    {
        var list = new List<KnownChannelRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT channel_id, 
                   COALESCE(title, 'Канал ' || channel_id), 
                   COALESCE(username, ''), 
                   COALESCE(owner_id, 0),
                   COALESCE(owner_username, ''),
                   COALESCE(is_active, 1)
            FROM known_channels
            WHERE is_active = 1;";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new KnownChannelRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt32(5) == 1
            ));
        }
        return list;
    }

    // Групповой подсчёт необработанных постов по каналам (замена N+1 запросов в /channels)
    public async Task<Dictionary<long, int>> GetPendingCountsAsync()
    {
        var result = new Dictionary<long, int>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT channel_id, COUNT(*) FROM channel_posts
            WHERE is_processed = 0
            GROUP BY channel_id;";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt64(0)] = reader.GetInt32(1);
        }
        return result;
    }

    // Авто-очистка старых обработанных постов (защита от раздувания БД)
    public async Task<int> DeleteProcessedPostsOlderThanAsync(DateTime olderThanUtc)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM channel_posts
            WHERE is_processed = 1 AND post_date < $date;";
        command.Parameters.AddWithValue("$date", olderThanUtc.ToString("o"));

        return await command.ExecuteNonQueryAsync();
    }
}