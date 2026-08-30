using Microsoft.Data.Sqlite;
using TgGeminiEngine.AiEngine;
using TgGeminiEngine.Domain;

namespace TgGeminiEngine.Infrastructure;

public class FactionRepository
{
    private readonly string _connectionString;

    public FactionRepository(string connectionString) => _connectionString = connectionString;

    public async Task<string> GetPassportAsync(long userId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT passport FROM user_states WHERE user_id = $id";
        command.Parameters.AddWithValue("$id", userId);

        var res = await command.ExecuteScalarAsync();
        return res != null && res != DBNull.Value ? (string)res : Prompts.DefaultPassport;
    }

    public async Task<string> GetPassportByChannelIdAsync(long channelId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT passport FROM user_states WHERE bound_channel_id = $cid
            UNION ALL
            SELECT passport FROM user_states WHERE user_id = $cid
            LIMIT 1;";
        command.Parameters.AddWithValue("$cid", channelId);

        var res = await command.ExecuteScalarAsync();
        return res != null && res != DBNull.Value ? (string)res : Prompts.DefaultPassport;
    }

    public async Task SavePassportAsync(long userId, string passport)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO user_states (user_id, passport) VALUES ($id, $passport)
            ON CONFLICT(user_id) DO UPDATE SET passport = excluded.passport;";
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$passport", passport);

        await command.ExecuteNonQueryAsync();
    }

    public async Task SavePassportForChannelAsync(long channelId, long userId, string passport)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        long targetUserId = userId != 0 ? userId : channelId;

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO user_states (user_id, passport, bound_channel_id) VALUES ($uid, $passport, $cid)
            ON CONFLICT(user_id) DO UPDATE SET passport = excluded.passport, bound_channel_id = excluded.bound_channel_id;";
        
        command.Parameters.AddWithValue("$uid", targetUserId);
        command.Parameters.AddWithValue("$cid", channelId);
        command.Parameters.AddWithValue("$passport", passport);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> GetBoundChannelAsync(long userId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bound_channel_id FROM user_states WHERE user_id = $id";
        command.Parameters.AddWithValue("$id", userId);

        var result = await command.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0;
    }

    public async Task SaveBoundChannelAsync(long userId, long channelId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO user_states (user_id, passport, bound_channel_id) 
            VALUES ($id, $passport, $cid)
            ON CONFLICT(user_id) DO UPDATE SET bound_channel_id = excluded.bound_channel_id;";
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$passport", Prompts.DefaultPassport);
        command.Parameters.AddWithValue("$cid", channelId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<FactionStateRecord>> GetAllBoundFactionsAsync()
    {
        var list = new List<FactionStateRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT 
                COALESCE(kc.owner_id, us.user_id, 0) AS final_user_id,
                COALESCE(us.passport, $defPassport) AS passport,
                kc.channel_id AS bound_channel_id
            FROM known_channels kc
            LEFT JOIN user_states us ON (us.bound_channel_id = kc.channel_id OR us.user_id = kc.owner_id)
            WHERE kc.is_active = 1;";

        command.Parameters.AddWithValue("$defPassport", Prompts.DefaultPassport);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new FactionStateRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2)
            ));
        }
        return list;
    }

    // Регистрация активности пользователя (не стирается при вайпе игры)
    public async Task RecordUserInteractionAsync(long userId, string? username, bool canReceiveDm = true)
    {
        if (userId <= 0) return;
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO bot_users (user_id, username, can_receive_dm, last_seen)
            VALUES ($uid, $uname, $candm, $seen)
            ON CONFLICT(user_id) DO UPDATE SET 
                username = COALESCE(excluded.username, bot_users.username),
                can_receive_dm = excluded.can_receive_dm,
                last_seen = excluded.last_seen;";
        command.Parameters.AddWithValue("$uid", userId);
        command.Parameters.AddWithValue("$uname", (object?)username ?? DBNull.Value);
        command.Parameters.AddWithValue("$candm", canReceiveDm ? 1 : 0);
        command.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("o"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsUserKnownToReceiveDmAsync(long userId)
    {
        if (userId <= 0) return false;
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT can_receive_dm FROM bot_users WHERE user_id = $uid;";
        command.Parameters.AddWithValue("$uid", userId);

        var res = await command.ExecuteScalarAsync();
        return res != null && res != DBNull.Value && Convert.ToInt32(res) == 1;
    }

    // Безопасный вайп: стирает только посты и сбрасывает паспорта на 1951 год
    public async Task WipeAllDataAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM channel_posts;
            UPDATE user_states SET passport = $defPassport;";
        command.Parameters.AddWithValue("$defPassport", Prompts.DefaultPassport);
        await command.ExecuteNonQueryAsync();
    }

    // VACUUM вынесен в отдельный метод: во время боевой команды он блокирует БД
    public async Task VacuumAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        await command.ExecuteNonQueryAsync();
    }
}