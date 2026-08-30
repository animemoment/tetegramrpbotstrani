using System.Text;
using Microsoft.Extensions.Logging;
using TgGeminiEngine.AiEngine;
using TgGeminiEngine.Domain;
using TgGeminiEngine.Infrastructure;
using TgGeminiEngine.Security;

namespace TgGeminiEngine.Services;

public record FactionTurnResult(
    long UserId,
    long ChannelId,
    string Summary,
    string Passport,
    FactionMetrics? Metrics = null
);

public record WorldTurnResult(
    string WorldGazette,
    List<FactionTurnResult> FactionResults
);

public class GameResolutionService
{
    private readonly ChannelPostRepository _postRepo;
    private readonly FactionRepository _factionRepo;
    private readonly FactionMetricsRepository _metricsRepo;
    private readonly GeminiClient _geminiClient;
    private readonly ILogger<GameResolutionService> _logger;
    private readonly SemaphoreSlim _worldTurnLock = new(1, 1);

    // 🌟 Точка отсчета: 28 августа 2026 года = 1951 год, 1-я четверть (1/4)
    private static readonly DateTime RpBaseDateUtc = GameConstants.RpBaseDateUtc;
    private const int BaseYear = GameConstants.BaseYear;
    private const double DaysPerQuarter = GameConstants.DaysPerQuarter; // 1 реальный день = 1 четверть года (4 дня = 1 год)

    public GameResolutionService(ChannelPostRepository postRepo, FactionRepository factionRepo, FactionMetricsRepository metricsRepo, GeminiClient geminiClient, ILogger<GameResolutionService> logger)
    {
        _postRepo = postRepo;
        _factionRepo = factionRepo;
        _metricsRepo = metricsRepo;
        _geminiClient = geminiClient;
        _logger = logger;
    }

    public static (int Year, int Quarter, string QuarterName) GetCurrentGameDate() => GetGameDateAt(DateTime.UtcNow);

    /// <summary>Игровая (RP) дата для конкретного момента времени (для хронологии постов).</summary>
    public static string GetGameDateFor(DateTime utc)
    {
        if (utc < RpBaseDateUtc) return $"01.01.{BaseYear}";

        double yearFraction = (utc - RpBaseDateUtc).TotalDays / (DaysPerQuarter * 4);
        int year = BaseYear + (int)Math.Floor(yearFraction);
        int dayOfYear = (int)Math.Floor((yearFraction - Math.Floor(yearFraction)) * 365.24);
        return new DateTime(year, 1, 1).AddDays(dayOfYear).ToString("dd.MM.yyyy");
    }

    private static (int Year, int Quarter, string QuarterName) GetGameDateAt(DateTime utc)
    {
        double daysPassed = (utc - RpBaseDateUtc).TotalDays;
        if (daysPassed < 0) daysPassed = 0;

        int totalQuartersPassed = (int)Math.Floor(daysPassed / DaysPerQuarter);

        // Корректная работа как в будущем, так и если расчет запущен ранее 28 августа
        int currentQuarterIndex = (totalQuartersPassed % 4 + 4) % 4; // 0..3
        int yearOffset = (int)Math.Floor((double)totalQuartersPassed / 4.0);

        int year = BaseYear + yearOffset;
        int quarter = currentQuarterIndex + 1; // 1..4

        string quarterName = currentQuarterIndex switch
        {
            0 => "I Четверть (Январь - Март)",
            1 => "II Четверть (Апрель - Июнь)",
            2 => "III Четверть (Июль - Сентябрь)",
            _ => "IV Четверть (Октябрь - Декабрь)"
        };

        return (year, quarter, quarterName);
    }

    // 4 корзины категоризации постов для анализа взаимодействий
    private const string CatMilitary = "ВОЕННЫЕ ДЕЙСТВИЯ";
    private const string CatEconomyMic = "ЭКОНОМИКА И ВПК";
    private const string CatDiplomacy = "ДИПЛОМАТИЯ И ТОРГОВЛЯ";
    private const string CatInternal = "ВНУТРЕННЯЯ ПОЛИТИКА И ОБЩЕСТВО";

    internal static string CategorizePost(string content)
    {
        string c = content.ToLowerInvariant();
        string[] militaryWords = ["воен", "войск", "арми", "фронт", "атак", "наступл", "оборон", "артилл", "танк", "бомбардиров", "капитул", "ультиматум", "милит"];
        string[] economyWords = ["впк", "завод", "фабрик", "производ", "добыч", "нефт", "стал", "уголь", "рудник", "бюджет", "казн", "ввп", "налог", "покупк", "продаж", "скупк", "вооруж"];
        string[] diplomacyWords = ["дипломат", "союз", "договор", "пакт", "переговор", "посол", "торгов", "бартер", "эмбарг", "санкци", "нейтралитет", "мирн", "границ", "визит", "альянс"];
        string[] internalWords = ["населен", "рабоч", "крест", "протест", "мятеж", "репресси", "внутренн", "реформ", "закон", "культур", "религ", "пропаганд", "школ", "больниц", "провинц", "повстан"];

        if (militaryWords.Any(w => c.Contains(w, StringComparison.Ordinal))) return CatMilitary;
        if (economyWords.Any(w => c.Contains(w, StringComparison.Ordinal))) return CatEconomyMic;
        if (diplomacyWords.Any(w => c.Contains(w, StringComparison.Ordinal))) return CatDiplomacy;
        if (internalWords.Any(w => c.Contains(w, StringComparison.Ordinal))) return CatInternal;
        return CatInternal;
    }

    // Компактное представление метрик для модели (вместо «сырого» JSON)
    internal static string FormatMetricsForPrompt(FactionMetrics? m)
    {
        if (m is null)
            return "Метрики: недоступны.";

        var parts = new List<string>();
        if (m.Gdp is not null) parts.Add($"ВВП ${m.Gdp:F1}M");
        if (m.Treasury is not null) parts.Add($"казна ${m.Treasury:F1}M");
        if (m.TradeBalance is not null) parts.Add($"торг.баланс {m.TradeBalance:F1}%");
        if (m.Population is not null) parts.Add($"население {m.Population:F0}");
        if (m.Army is not null) parts.Add($"армия {m.Army:F0}");
        if (m.Tanks is not null) parts.Add($"танки/БТР {m.Tanks:F0}");
        if (m.Artillery is not null) parts.Add($"артиллерия {m.Artillery:F0}");
        if (m.Planes is not null) parts.Add($"самолёты {m.Planes:F0}");
        if (m.Ships is not null) parts.Add($"корабли {m.Ships:F0}");
        if (m.Oil is not null) parts.Add($"нефть {m.Oil:F1} тыс");
        if (m.Steel is not null) parts.Add($"сталь {m.Steel:F1} тыс");
        if (m.Coal is not null) parts.Add($"уголь {m.Coal:F1} тыс");
        if (m.Instability is not null) parts.Add($"нестабильность {m.Instability:F0}%");
        if (m.RebellionRisk is not null) parts.Add($"риск мятежей {m.RebellionRisk:F0}%");
        if (m.Wars is { Count: > 0 }) parts.Add($"войны: {string.Join(", ", m.Wars)}");
        if (m.Allies is { Count: > 0 }) parts.Add($"союзники: {string.Join(", ", m.Allies)}");
        if (m.Enemies is { Count: > 0 }) parts.Add($"враги: {string.Join(", ", m.Enemies)}");
        if (m.Treaties is { Count: > 0 }) parts.Add($"договоры: {string.Join(", ", m.Treaties)}");

        return parts.Count > 0 ? "Метрики: " + string.Join("; ", parts) + "." : "Метрики: недоступны.";
    }

    public async Task<(string Summary, string Passport, int ProcessedCount)> ResolveChannelTurnAsync(
        long userId, long channelId, string interactionReport = "", int maxPosts = int.MaxValue, bool persist = true)
    {
        var rawPosts = await _postRepo.GetUnprocessedPostsAsync(channelId);
        if (maxPosts > 0 && rawPosts.Count > maxPosts)
            rawPosts = rawPosts.TakeLast(maxPosts).ToList();

        if (rawPosts.Count == 0)
            return (string.Empty, string.Empty, 0);

        var validPosts = new List<ChannelPostRecord>();
        int maxId = 0;
        foreach (var post in rawPosts)
        {
            if (post.MessageId > maxId) maxId = post.MessageId;
            var val = PreLlmShield.ValidatePost(post.Content);
            if (val.IsValid)
            {
                validPosts.Add(post);
            }
        }

        if (validPosts.Count == 0)
        {
            if (persist)
                await _postRepo.MarkPostsAsProcessedAsync(channelId, maxId);
            return ("Предупреждение: Посты не прошли спам-фильтр.", string.Empty, 0);
        }

        var (gameYear, quarter, quarterName) = GetCurrentGameDate();

        var sbLogs = new StringBuilder();
        foreach (var post in validPosts)
        {
            sbLogs.AppendLine($"--- [Пост #{post.MessageId} | Игровая дата: {GetGameDateFor(post.PostDate)}] ---");
            sbLogs.AppendLine(post.Content);
            sbLogs.AppendLine();
        }

        string currentPassport = await _factionRepo.GetPassportByChannelIdAsync(channelId);
string interactionBlock = string.IsNullOrWhiteSpace(interactionReport)
            ? string.Empty
            : $"=== ОТЧЁТ О ВЗАИМОДЕЙСТВИЯХ ГОСУДАРСТВ ===\n{interactionReport}\n\n";
        currentPassport = TruncatePassport(currentPassport);
        string fullPrompt = 
            $"=== ТЕКУЩАЯ ИГРОВАЯ ДАТА: {gameYear} ГОД (Четверть {quarter}/4: {quarterName}) ===\n" +
            $"=== ВАЖНО: Вымышленный мир (не Земля), география неизвестна, строго 1950-е годы ===\n\n" +
            $"=== ТЕКУЩИЙ ПАСПОРТ ГОСУДАРСТВА ===\n{currentPassport}\n\n" +
            interactionBlock +
            $"=== ХРОНОЛОГИЧЕСКИЙ ЖУРНАЛ ПРИКАЗОВ И ВПК ИЗ КАНАЛА ===\n{sbLogs}";

        string aiResponse = await _geminiClient.GenerateContentWithFallbackAsync(Prompts.SystemEnginePrompt, fullPrompt);

        if (aiResponse == "SAFETY_BLOCKED")
        {
            string safeSummary = 
                "ВОЕННО-ИСТОРИЧЕСКАЯ ХРОНИКА:\n" +
                "- Внимание: Ряд радикальных директив верховного командования вызвал ожесточенное сопротивление партизан и мятежников.\n" +
                "- Подразделения на местах провели жесткую зачистку, что привело к росту социальной напряженности.\n\n" +
                "СВОДКА ГЕНЕРАЛЬНОГО ШТАБА И ВПК:\n" +
                "- Верификация приказов: ВЫПОЛНЕНО ЧАСТИЧНО (Всплеск партизанской активности)\n" +
                "- Экономический аудит: Падение стабильности на 15%, рост расходов на гарнизоны.\n" +
                "- Хештег фракции: #Кризис_GreenwellRP";

            if (persist)
                await _postRepo.MarkPostsAsProcessedAsync(channelId, maxId);
            return (safeSummary, currentPassport, validPosts.Count);
        }

        // Структурированный разбор: вердикт + паспорт + JSON-блок метрик
        var parsed = FactionTurnParser.Parse(aiResponse, currentPassport);
        string summary = parsed.Summary;
        string newPassport = parsed.Passport;
        FactionMetrics? metrics = parsed.Metrics;

        if (persist)
        {
            if (newPassport != currentPassport)
                await _factionRepo.SavePassportForChannelAsync(channelId, userId, newPassport);

            if (metrics is not null)
                await _metricsRepo.SaveQuarterMetricsAsync(userId, gameYear, quarter, metrics, summary);

            await _postRepo.MarkPostsAsProcessedAsync(channelId, maxId);
        }

        return (summary, newPassport, validPosts.Count);
    }

    public async Task<WorldTurnResult> ResolveWorldTurnAsync()
    {
        return await ResolveWorldTurnWithLockAsync(int.MaxValue, persist: true);
    }

    // 🧪 Сухой прогон для /test: без записи в БД, без рассылки ЛС и газеты
    public async Task<WorldTurnResult> ResolveWorldTurnDryRunAsync(int maxPostsPerFaction)
    {
        return await ResolveWorldTurnWithLockAsync(maxPostsPerFaction, persist: false);
    }

    private async Task<WorldTurnResult> ResolveWorldTurnWithLockAsync(int maxPostsPerFaction, bool persist)
    {
        // Защита от двойного запуска: параллельный вызов получит предупреждение
        if (!await _worldTurnLock.WaitAsync(0))
        {
            return new WorldTurnResult("Предупреждение: Расчёт мирового хода уже выполняется. Дождитесь завершения.", []);
        }

        try
        {
            return await ResolveWorldTurnInternalAsync(maxPostsPerFaction, persist);
        }
        finally
        {
            _worldTurnLock.Release();
        }
    }

    private async Task<WorldTurnResult> ResolveWorldTurnInternalAsync(int maxPostsPerFaction, bool persist)
    {
        var factions = await _factionRepo.GetAllBoundFactionsAsync();
        var uniqueChannels = factions.GroupBy(f => f.BoundChannelId).Select(g => g.First()).ToList();

        var results = new List<FactionTurnResult>();
        if (uniqueChannels.Count == 0)
            return new WorldTurnResult("Предупреждение: В базе нет обнаруженных каналов.", results);

        var (gameYear, quarter, quarterName) = GetCurrentGameDate();

        // Этап анализа взаимодействий: один вызов Gemini по паспортам + категоризированным постам
        string interactionReport = await BuildInteractionReportAsync(uniqueChannels, gameYear, quarterName);

        var sbAllSummaries = new StringBuilder();
        var lockObj = new object();

        using var semaphore = new SemaphoreSlim(4);

        var tasks = uniqueChannels.Select(async faction =>
        {
            await semaphore.WaitAsync();
            try
            {
                var (summary, passport, count) = await ResolveChannelTurnAsync(
                    faction.UserId, faction.BoundChannelId, interactionReport, maxPostsPerFaction, persist);
                if (count > 0 && !string.IsNullOrWhiteSpace(summary))
                {
                    lock (lockObj)
                    {
                        results.Add(new FactionTurnResult(faction.UserId, faction.BoundChannelId, summary, passport));
                        sbAllSummaries.AppendLine($"=== СВОДКА ГОСУДАРСТВА (Канал ID: {faction.BoundChannelId}) ===");
                        sbAllSummaries.AppendLine(summary);
                        sbAllSummaries.AppendLine();
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (results.Count == 0)
            return new WorldTurnResult("Информация: Ни в одном канале нет новых постов для расчета.", results);

        // Обогащаем сводки метриками для газеты (модель получает цифры, а не «сырой» JSON)
        foreach (var r in results)
        {
            sbAllSummaries.AppendLine($"=== МЕТРИКИ ГОСУДАРСТВА (Канал ID: {r.ChannelId}) ===");
            sbAllSummaries.AppendLine(FormatMetricsForPrompt(r.Metrics));
            sbAllSummaries.AppendLine();
        }

        // Газета: один вызов Gemini, внутри — три раздела (POLITICS / ECONOMY / SOCIETY)
        string gazettePrompt = Prompts.BuildWorldGazettePrompt(gameYear, $"{quarter}/4: {quarterName}", sbAllSummaries.ToString());

        string worldGazette = await _geminiClient.GenerateContentWithFallbackAsync(
            "Ты — Главный Аналитик международного военно-стратегического бюллетеня 1950-х годов в вымышленном мире.",
            gazettePrompt,
            maxOutputTokens: 16384
        );

        return new WorldTurnResult(worldGazette, results);
    }
// Собирает паспорта + посты всех каналов, категоризирует их и просит модель
    // проверить межгосударственные взаимодействия (торговля, дипломатия, конфликты, хронология).
    private async Task<string> BuildInteractionReportAsync(List<FactionStateRecord> uniqueChannels, int gameYear, string quarterName)
    {
        if (uniqueChannels.Count < 2)
            return string.Empty;

        try
        {
            var sb = new StringBuilder();
            var postsByChannel = new Dictionary<long, List<CategorizedPostRecord>>();

            foreach (var faction in uniqueChannels)
            {
                var posts = await _postRepo.GetUnprocessedPostsAsync(faction.BoundChannelId);
                var categorized = posts
                    .Where(p => PreLlmShield.ValidatePost(p.Content).IsValid)
                    .Select(p => new CategorizedPostRecord(p, CategorizePost(p.Content), GetGameDateFor(p.PostDate)))
                    .ToList();
                postsByChannel[faction.BoundChannelId] = categorized;
            }

            foreach (var faction in uniqueChannels)
            {
                string passport = await _factionRepo.GetPassportByChannelIdAsync(faction.BoundChannelId);
                passport = TruncatePassport(passport, 1500);

                sb.AppendLine($"--- ГОСУДАРСТВО (Канал ID: {faction.BoundChannelId}) ---");
                sb.AppendLine("ПАСПОРТ:");
                sb.AppendLine(passport);
                sb.AppendLine();

                if (postsByChannel.TryGetValue(faction.BoundChannelId, out var posts) && posts.Count > 0)
                {
                    sb.AppendLine("ПОСТЫ (категория | игровая дата | текст):");
                    foreach (var p in posts.Take(15))
                    {
                        string text = p.Post.Content.Length > 700 ? p.Post.Content[..700] + "…" : p.Post.Content;
                        sb.AppendLine($"[{p.Category} | {p.RpDateLabel}] {text}");
                    }
                }
                else
                {
                    sb.AppendLine("ПОСТЫ: нет новых приказов.");
                }
                sb.AppendLine();
            }

            string prompt = Prompts.BuildInteractionReportPrompt(gameYear, quarterName, sb.ToString());
            string raw = await _geminiClient.GenerateContentWithFallbackAsync(
                "Ты — Арбитр взаимодействий государств в вымышленном мире 1950-х годов.",
                prompt,
                maxOutputTokens: 8192
            );

            int start = raw.IndexOf("===INTERACTION_REPORT===", StringComparison.Ordinal);
            int end = raw.IndexOf("===END_INTERACTION_REPORT===", StringComparison.Ordinal);
            if (start < 0 || end <= start)
                return string.Empty;

            return raw.Substring(start + "===INTERACTION_REPORT===".Length, end - start - "===INTERACTION_REPORT===".Length).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Анализ взаимодействий не выполнен (продолжаем без него)");
            return string.Empty;
        }
    }

    // Защита от раздувания паспорта: модель может превысить лимит контекста

    // Защита от раздувания паспорта: модель может превысить лимит контекста
    private static string TruncatePassport(string passport, int maxLength = 3000)
    {
        if (string.IsNullOrEmpty(passport) || passport.Length <= maxLength)
            return passport;

        return passport[..maxLength] + "\n... [паспорт обрезан для экономии контекста]";
    }
}