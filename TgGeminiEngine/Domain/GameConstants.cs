namespace TgGeminiEngine.Domain;

// Центральный реестр игровых констант (раньше были продублированы в нескольких классах)
public static class GameConstants
{
    // RP-календарь: 1 реальный день = 1 четверть, 4 дня = 1 год
    public static readonly DateTime RpStartDateUtc = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RpBaseDateUtc = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
    public const int BaseYear = 1951;
    public const double DaysPerQuarter = 1.0;

    // Администратор и канал публикации мировой газеты
    public const string AdminUsername = "FanTheAnime";
    public const string WorldGazetteChannel = "@WorldGazet";

    // Кулдаун тяжёлых команд
    public static readonly TimeSpan HeavyCommandCooldown = TimeSpan.FromSeconds(5);

    // Защита контекста Gemini
    public const int MaxPassportLength = 3000;
}