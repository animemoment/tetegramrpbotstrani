using Microsoft.Data.Sqlite;

namespace TgGeminiEngine.Infrastructure;

public class DatabaseInitializer
{
    private readonly string _connectionString;

    public DatabaseInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;

            -- Паспорта и привязки игры
            CREATE TABLE IF NOT EXISTS user_states (
                user_id INTEGER PRIMARY KEY,
                passport TEXT NOT NULL,
                bound_channel_id INTEGER DEFAULT 0
            );

            -- Посты из каналов
            CREATE TABLE IF NOT EXISTS channel_posts (
                channel_id INTEGER NOT NULL,
                message_id INTEGER NOT NULL,
                post_date TEXT NOT NULL,
                content TEXT NOT NULL,
                is_processed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (channel_id, message_id)
            );

            -- Реестр каналов
            CREATE TABLE IF NOT EXISTS known_channels (
                channel_id INTEGER PRIMARY KEY,
                title TEXT,
                username TEXT,
                owner_id INTEGER DEFAULT 0,
                owner_username TEXT DEFAULT '',
                is_active INTEGER DEFAULT 1
            );

            -- Нестираемая таблица пользователей, которые когда-либо взаимодействовали с ботом
            CREATE TABLE IF NOT EXISTS bot_users (
                user_id INTEGER PRIMARY KEY,
                username TEXT,
                can_receive_dm INTEGER DEFAULT 1,
                last_seen TEXT
            );

            -- Структурированные метрики государства по кварталам (для годовых отчётов и газеты)
            CREATE TABLE IF NOT EXISTS faction_metrics (
                user_id INTEGER NOT NULL,
                year INTEGER NOT NULL,
                quarter INTEGER NOT NULL,
                metrics_json TEXT,
                summary TEXT,
                created_at TEXT NOT NULL,
                PRIMARY KEY (user_id, year, quarter)
            );";
        await command.ExecuteNonQueryAsync();

        await TryAddColumnAsync(connection, "known_channels", "owner_id", "INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection, "known_channels", "owner_username", "TEXT DEFAULT ''");
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string table, string column, string type)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Колонка уже существует
        }
    }
}   