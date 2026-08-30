using System.Text;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgGeminiEngine.Domain;
using TgGeminiEngine.Infrastructure;
using TgGeminiEngine.Services;

namespace TgGeminiEngine.Telegram;

// Панель управления администратора: реестры, скан, расчёт хода, сброс базы
public class AdminCommandHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ChannelPostRepository _postRepo;
    private readonly FactionRepository _factionRepo;
    private readonly GameResolutionService _resolutionService;
    private readonly YearReportService _yearReportService;
    private readonly ChannelScanner _scanner;
    private readonly VerdictDispatcher _dispatcher;
    private readonly ILogger<AdminCommandHandler> _logger;

    private DateTime? _wipeRequestedAtUtc;

    public AdminCommandHandler(
        ITelegramBotClient botClient,
        ChannelPostRepository postRepo,
        FactionRepository factionRepo,
        GameResolutionService resolutionService,
        YearReportService yearReportService,
        ChannelScanner scanner,
        VerdictDispatcher dispatcher,
        ILogger<AdminCommandHandler> logger)
    {
        _botClient = botClient;
        _postRepo = postRepo;
        _factionRepo = factionRepo;
        _resolutionService = resolutionService;
        _yearReportService = yearReportService;
        _scanner = scanner;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    // 📋 Команда /users: Онлайн-тест ЛС для каждого канала
    public async Task HandleSyncAndListUsersCommandAsync(long adminChatId, CancellationToken ct)
    {
        await _botClient.SendTextMessageAsync(adminChatId, "🔍 **Проверяю владельцев каналов и тестирую соединение в ЛС...**", parseMode: ParseMode.Markdown, cancellationToken: ct);

        var channels = await _postRepo.GetAllKnownChannelsAsync();
        if (channels.Count == 0)
        {
            await _botClient.SendTextMessageAsync(adminChatId, "ℹ️ В базе нет зарегистрированных каналов.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"👥 **РЕЕСТР ДОСТАВКИ ВЕРДИКТОВ ({channels.Count} каналов)**\n━━━━━━━━━━━━━━━━━━━━\n");

        int readyDm = 0;
        int channelFallbackCount = 0;

        foreach (var c in channels)
        {
            long ownerId = c.OwnerId;
            string ownerUname = c.OwnerUsername;

            var detected = await _scanner.TryDetectChannelOwnerAsync(c.ChannelId, ct);
            if (detected.OwnerId != 0)
            {
                ownerId = detected.OwnerId;
                ownerUname = detected.OwnerUsername;
                await _postRepo.UpdateChannelOwnerAsync(c.ChannelId, ownerId, ownerUname);
            }

            string channelLabel = !string.IsNullOrWhiteSpace(c.Username) ? $"@{c.Username}" : c.Title;
            sb.AppendLine($"📌 **{channelLabel}** (`{c.ChannelId}`)");

            if (ownerId != 0)
            {
                string userLink = !string.IsNullOrWhiteSpace(ownerUname) ? $"@{ownerUname}" : $"ID: `{ownerId}`";

                // Проводим реальный тест через SendChatAction
                bool canDm = await _scanner.CheckIfUserCanReceiveDmAsync(ownerId, ct);

                if (canDm)
                {
                    sb.AppendLine($"   ├ 👤 Владелец: {userLink} (ID: `{ownerId}`)");
                    sb.AppendLine($"   └ 🟢 **ЛС доступно** (вердикт придет в личку)");
                    readyDm++;
                }
                else
                {
                    sb.AppendLine($"   ├ 👤 Владелец: {userLink} (ID: `{ownerId}`)");
                    sb.AppendLine($"   └ 🟡 **ЛС закрыто** (вердикт автоматически выгрузится в его канал)");
                    channelFallbackCount++;
                }
            }
            else
            {
                sb.AppendLine($"   └ 🟡 **Бот не админ** (вердикт будет опубликован в канал)");
                channelFallbackCount++;
            }

            sb.AppendLine();
        }

        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━");
        sb.AppendLine($"📊 **Итог:** Доставка в ЛС: **{readyDm}** | Авто-доставка в канал: **{channelFallbackCount}**");
        sb.AppendLine("\n💡 *Ни один отчет не пропадет: если игрок не открыл ЛС с ботом, вердикт придет прямо в его канал фракции.*");

        await _dispatcher.SendLongMessageToChatIdAsync(adminChatId, sb.ToString(), ct);
    }

    // 📋 Команда /channels: Реестр каналов и очередь постов
    public async Task HandleListChannelsCommandAsync(long adminChatId, CancellationToken ct)
    {
        var channels = await _postRepo.GetAllKnownChannelsAsync();
        if (channels.Count == 0)
        {
            await _botClient.SendTextMessageAsync(adminChatId, "ℹ️ В базе нет зарегистрированных каналов.", cancellationToken: ct);
            return;
        }

        // Один групповой запрос вместо N+1
        var pendingCounts = await _postRepo.GetPendingCountsAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"📋 **РЕЕСТР КАНАЛОВ (Всего: {channels.Count})**\n━━━━━━━━━━━━━━━━━━━━\n");

        int index = 1;
        foreach (var c in channels)
        {
            string label = !string.IsNullOrWhiteSpace(c.Username) ? $"@{c.Username} ({c.Title})" : $"{c.Title} (`{c.ChannelId}`)";
            string ownerInfo = c.OwnerId != 0
                ? $"Владелец: [ID {c.OwnerId}](tg://user?id={c.OwnerId}) {(!string.IsNullOrWhiteSpace(c.OwnerUsername) ? "(@" + c.OwnerUsername + ")" : "")}"
                : "Владелец: *Определяется автоматически*";

            int pending = pendingCounts.TryGetValue(c.ChannelId, out int p) ? p : 0;
            sb.AppendLine($"**{index}. {label}**");
            sb.AppendLine($"   • ID: `{c.ChannelId}` | {ownerInfo}");
            sb.AppendLine($"   • В очереди на расчет: **{pending}** постов\n");
            index++;
        }

        await _dispatcher.SendLongMessageToChatIdAsync(adminChatId, sb.ToString(), ct);
    }

    // 🚀 Команда /scan_all: Параллельный сбор постов
    public async Task HandleScanAllChannelsParallelCommandAsync(long adminChatId, CancellationToken ct)
    {
        var channels = await _postRepo.GetAllKnownChannelsAsync();
        if (channels.Count == 0)
        {
            await _botClient.SendTextMessageAsync(adminChatId, "ℹ️ Нет обнаруженных каналов для сканирования.", cancellationToken: ct);
            return;
        }

        await _botClient.SendTextMessageAsync(adminChatId, $"⏳ Запускаю параллельный скан {channels.Count} каналов...", parseMode: ParseMode.Markdown, cancellationToken: ct);

        int total = 0;
        var failedChannels = new List<string>();
        var lockObj = new object();

        using var semaphore = new SemaphoreSlim(5);

        var tasks = channels.Select(async c =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                int count = await _scanner.RobustCollectHistoryAsync(adminChatId, c.ChannelId, ct);
                if (count >= 0)
                {
                    Interlocked.Add(ref total, count);
                }
                else
                {
                    string cName = !string.IsNullOrWhiteSpace(c.Username) ? $"@{c.Username}" : c.Title;
                    lock (lockObj)
                    {
                        failedChannels.Add($"• **{cName}** (`{c.ChannelId}`)");
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var sbResult = new StringBuilder();
        sbResult.AppendLine($"✅ **Скан завершен!** Собрано новых постов: **{total}**.\n");

        if (failedChannels.Count > 0)
        {
            sbResult.AppendLine("🚨 **КАНАЛЫ БЕЗ ПРАВ АДМИНИСТРАТОРА У БОТА:**");
            foreach (var f in failedChannels) sbResult.AppendLine(f);
        }

        await _dispatcher.SendLongMessageToChatIdAsync(adminChatId, sbResult.ToString(), ct);
    }

    // 🚀 Глобальный расчет хода с гарантированной доставкой (ЛС -> Канал)
    public async Task HandleGlobalTurnCommandAsync(long adminChatId, CancellationToken ct)
    {
        await _botClient.SendTextMessageAsync(adminChatId, "⏳ **Запущен расчет хода (1951 год, 4 четверти)...**", parseMode: ParseMode.Markdown, cancellationToken: ct);
        await _botClient.SendChatActionAsync(adminChatId, ChatAction.Typing, cancellationToken: ct);

        var channels = await _postRepo.GetAllKnownChannelsAsync();
        foreach (var c in channels)
        {
            if (c.OwnerId == 0)
            {
                var det = await _scanner.TryDetectChannelOwnerAsync(c.ChannelId, ct);
                if (det.OwnerId != 0)
                {
                    await _postRepo.UpdateChannelOwnerAsync(c.ChannelId, det.OwnerId, det.OwnerUsername);
                }
            }
        }

        using var scanSemaphore = new SemaphoreSlim(5);
        var scanTasks = channels.Select(async c =>
        {
            await scanSemaphore.WaitAsync(ct);
            try
            {
                await _scanner.RobustCollectHistoryAsync(adminChatId, c.ChannelId, ct);
            }
            finally
            {
                scanSemaphore.Release();
            }
        });
        await Task.WhenAll(scanTasks);

        var result = await _resolutionService.ResolveWorldTurnAsync();

        if (result.FactionResults.Count == 0)
        {
            await _botClient.SendTextMessageAsync(adminChatId, result.WorldGazette, cancellationToken: ct);
            return;
        }

        int deliveredDm = 0;
        int deliveredChannel = 0;
        var dispatchLogs = new List<string>();

        // Рассылка персональных вердиктов
        var sendTasks = result.FactionResults.Select(async factionResult =>
        {
            string metricsText = GameResolutionService.FormatMetricsForPrompt(factionResult.Metrics);
            string reportText = $"🏛️ **ИТОГИ ХОДА ВАШЕГО ГОСУДАРСТВА ({GameConstants.BaseYear} год)**\n\n{factionResult.Summary}\n\n**📊 МЕТРИКИ ГОСУДАРСТВА:**\n{metricsText}\n\n*Паспорт обновлен. Для просмотра введите /passport в боте.*";
            bool dmSuccess = false;

            // 1. Пробуем доставить в ЛС
            if (factionResult.UserId > 0)
            {
                try
                {
                    await _dispatcher.SendLongMessageToChatIdAsync(factionResult.UserId, reportText, ct);
                    Interlocked.Increment(ref deliveredDm);
                    dmSuccess = true;
                    lock (dispatchLogs)
                    {
                        dispatchLogs.Add($"• Игрок [ID {factionResult.UserId}](tg://user?id={factionResult.UserId}) — 🟢 Доставлено в ЛС");
                    }
                }
                catch
                {
                    dmSuccess = false;
                }
            }

            // 2. Если ЛС закрыто или не найдено — отправляем вердикт прямо в канал фракции
            if (!dmSuccess && factionResult.ChannelId != 0)
            {
                try
                {
                    await _dispatcher.SendLongMessageToChatIdAsync(factionResult.ChannelId, reportText, ct);
                    Interlocked.Increment(ref deliveredChannel);
                    lock (dispatchLogs)
                    {
                        dispatchLogs.Add($"• Канал `{factionResult.ChannelId}` — 🟡 Выгружено в канал фракции (ЛС игрока закрыто)");
                    }
                }
                catch (Exception ex)
                {
                    lock (dispatchLogs)
                    {
                        dispatchLogs.Add($"• Канал `{factionResult.ChannelId}` — 🔴 Ошибка отправки: {ex.Message}");
                    }
                }
            }
        });

        await Task.WhenAll(sendTasks);

        // Публикация общей Газеты: три отдельных поста (политика/экономика/общество)
        try
        {
            var (gazettePolitics, gazetteEconomy, gazetteSociety) = SplitGazetteSections(result.WorldGazette);

            string header = "🗞️ **ВЫПУСК МИРОВОЙ ГАЗЕТЫ (WORLD GAZETTE)**\n\n";
            if (!string.IsNullOrWhiteSpace(gazettePolitics))
                await _dispatcher.SendLongMessageAsync(GameConstants.WorldGazetteChannel, header + gazettePolitics, ct);
            if (!string.IsNullOrWhiteSpace(gazetteEconomy))
                await _dispatcher.SendLongMessageAsync(GameConstants.WorldGazetteChannel, header + gazetteEconomy, ct);
            if (!string.IsNullOrWhiteSpace(gazetteSociety))
                await _dispatcher.SendLongMessageAsync(GameConstants.WorldGazetteChannel, header + gazetteSociety, ct);

            var sbReport = new StringBuilder();
            sbReport.AppendLine($"📰 **Газета опубликована в {GameConstants.WorldGazetteChannel} (3 поста)!**\n");
            sbReport.AppendLine($"• Раздел политики: **{gazettePolitics.Length:N0} симв.**");
            sbReport.AppendLine($"• Раздел экономики: **{gazetteEconomy.Length:N0} симв.**");
            sbReport.AppendLine($"• Раздел общества: **{gazetteSociety.Length:N0} симв.**\n");
            sbReport.AppendLine($"• Доставлено лично в ЛС: **{deliveredDm}**");
            sbReport.AppendLine($"• Выгружено в каналы фракций: **{deliveredChannel}**\n");
            sbReport.AppendLine("📋 **Детализация доставки:**");
            foreach (var log in dispatchLogs)
            {
                sbReport.AppendLine(log);
            }

            await _dispatcher.SendLongMessageToChatIdAsync(adminChatId, sbReport.ToString(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка публикации в {Channel}", GameConstants.WorldGazetteChannel);
            await _botClient.SendTextMessageAsync(adminChatId, $"⚠️ Ошибка публикации в {GameConstants.WorldGazetteChannel}: `{ex.Message}`", parseMode: ParseMode.Markdown, cancellationToken: ct);
        }
    }
// 🧪 Команда /test [N]: сухой прогон без записи в БД и без рассылки игрокам;
    // результаты публикуются в тестовый канал @testbotchannel1000
    public async Task HandleTestCommandAsync(long adminChatId, int maxPostsPerFaction, CancellationToken ct)
    {
        await _botClient.SendTextMessageAsync(adminChatId,
            $"⏳ **Сухой прогон хода (max {maxPostsPerFaction} постов/фракция)...**",
            parseMode: ParseMode.Markdown, cancellationToken: ct);

        var result = await _resolutionService.ResolveWorldTurnDryRunAsync(maxPostsPerFaction);

        if (result.FactionResults.Count == 0)
        {
            await _botClient.SendTextMessageAsync(adminChatId, result.WorldGazette, cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("🧪 **ДИАГНОСТИКА СУХОГО ПРОГОНА**\n━━━━━━━━━━━━━━━━━━━━\n");
        sb.AppendLine($"• Фракций обработано: **{result.FactionResults.Count}**");
        sb.AppendLine($"• Газета: {result.WorldGazette.Length:N0} символов (лимит: 10 000)\n");

        foreach (var r in result.FactionResults)
        {
            sb.AppendLine($"**Канал `{r.ChannelId}`** (ID {r.UserId}):");
            sb.AppendLine($"   • Вердикт: {r.Summary.Length:N0} симв.");
            sb.AppendLine($"   • Паспорт: {(r.Passport.Length > 50 ? r.Passport.Length + " симв." : "не изменился")}");
            sb.AppendLine($"   • Метрики: {GameResolutionService.FormatMetricsForPrompt(r.Metrics)}");
            sb.AppendLine();
        }

        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━");
        sb.AppendLine("💡 *Это был сухой прогон: БД не изменена, рассылок игрокам не было.*");
        await _dispatcher.SendLongMessageToChatIdAsync(adminChatId, sb.ToString(), ct);

        // Публикация результатов прогона в тестовый канал @testbotchannel1000
        try
        {
            var (testPolitics, testEconomy, testSociety) = SplitGazetteSections(result.WorldGazette);
            string testHeader = "🧪 **ТЕСТОВЫЙ ВЫПУСК (СУХОЙ ПРОГОН)**\n\n";
            if (!string.IsNullOrWhiteSpace(testPolitics))
                await _dispatcher.SendLongMessageAsync("@testbotchannel1000", testHeader + testPolitics, ct);
            if (!string.IsNullOrWhiteSpace(testEconomy))
                await _dispatcher.SendLongMessageAsync("@testbotchannel1000", testHeader + testEconomy, ct);
            if (!string.IsNullOrWhiteSpace(testSociety))
                await _dispatcher.SendLongMessageAsync("@testbotchannel1000", testHeader + testSociety, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка публикации тестового выпуска в @testbotchannel1000");
            await _botClient.SendTextMessageAsync(adminChatId, $"⚠️ Ошибка публикации в тестовый канал: `{ex.Message}`", parseMode: ParseMode.Markdown, cancellationToken: ct);
        }
    }

// 📅 Команда /year_report [год]: персональные годовые отчёты игрокам
    public async Task HandleYearReportCommandAsync(long adminChatId, int year, CancellationToken ct)
    {
        await _botClient.SendTextMessageAsync(adminChatId, $"⏳ **Формирую годовые отчёты за {year} год...**", parseMode: ParseMode.Markdown, cancellationToken: ct);

        var report = await _yearReportService.BuildYearReportsAsync(year, ct);
        if (report.Count == 0)
        {
            await _botClient.SendTextMessageAsync(adminChatId, "ℹ️ За указанный год нет данных метрик ни по одной фракции.", cancellationToken: ct);
            return;
        }

        int sent = 0;
        var errors = new List<string>();
        foreach (var (userId, channelId, reportText) in report)
        {
            bool dmSuccess = false;
            if (userId > 0)
            {
                try
                {
                    await _dispatcher.SendLongMessageToChatIdAsync(userId, reportText, ct);
                    sent++;
                    dmSuccess = true;
                }
                catch
                {
                    dmSuccess = false;
                }
            }

            if (!dmSuccess && channelId != 0)
            {
                try
                {
                    await _dispatcher.SendLongMessageToChatIdAsync(channelId, reportText, ct);
                    sent++;
                }
                catch (Exception ex)
                {
                    errors.Add($"• Канал `{channelId}`: {ex.Message}");
                }
            }
        }

        var sbSummary = new StringBuilder();
        sbSummary.AppendLine($"📅 **Годовые отчёты за {year} год разосланы.**\n");
        sbSummary.AppendLine($"• Отчётов сформировано: **{report.Count}**");
        sbSummary.AppendLine($"• Доставлено: **{sent}**");
        if (errors.Count > 0)
        {
            sbSummary.AppendLine("\n⚠️ **Ошибки доставки:**");
            foreach (var e in errors) sbSummary.AppendLine(e);
        }
        await _dispatcher.SendLongMessageToChatIdAsync(adminChatId, sbSummary.ToString(), ct);
    }

    // Разбивает выпуск газеты на три поста по маркерам ===POLITICS=== / ===ECONOMY=== / ===SOCIETY===
    private static (string Politics, string Economy, string Society) SplitGazetteSections(string gazette)
    {
        string ExtractSection(string marker)
        {
            int start = gazette.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += marker.Length;

            string[] nextMarkers = ["===POLITICS===", "===ECONOMY===", "===SOCIETY==="];
            int end = int.MaxValue;
            foreach (var next in nextMarkers)
            {
                int idx = gazette.IndexOf(next, start, StringComparison.Ordinal);
                if (idx >= 0 && idx < end) end = idx;
            }
            if (end == int.MaxValue) end = gazette.Length;
            return gazette[start..end].Trim();
        }

        string politics = ExtractSection("===POLITICS===");
        string economy = ExtractSection("===ECONOMY===");
        string society = ExtractSection("===SOCIETY===");

        // Failsafe: если маркеры не найдены — публикуем весь текст первым постом
        if (string.IsNullOrWhiteSpace(politics) && string.IsNullOrWhiteSpace(economy) && string.IsNullOrWhiteSpace(society))
            return (gazette, string.Empty, string.Empty);

        return (politics, economy, society);
    }

    // ⚠️ Команда /wipe_all: Запрос сброса базы
    // ⚠️ Команда /wipe_all: Запрос сброса базы

    // ⚠️ Команда /wipe_all: Запрос сброса базы
    public async Task RequestWipeAsync(long adminChatId, CancellationToken ct)
    {
        _wipeRequestedAtUtc = DateTime.UtcNow;
        await _botClient.SendTextMessageAsync(adminChatId,
            "⚠️ **ЗАПРОШЕНО ПОЛНОЕ УДАЛЕНИЕ БАЗЫ ДАННЫХ!**\n\n" +
            "Посты и прогресс будут сброшены на 1951 год.\n" +
            "База игроков и каналов СОХРАНЯЕТСЯ.\n" +
            "Для подтверждения введите `/wipe_confirm` в течение **60 секунд**.",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    // 🗑️ Команда /wipe_confirm: Подтверждение сброса базы
    public async Task ConfirmWipeAsync(long adminChatId, CancellationToken ct)
    {
        if (_wipeRequestedAtUtc.HasValue && (DateTime.UtcNow - _wipeRequestedAtUtc.Value).TotalSeconds <= 60)
        {
            _wipeRequestedAtUtc = null;
            await _factionRepo.WipeAllDataAsync();
            await _botClient.SendTextMessageAsync(adminChatId, "🗑️ **База данных очищена.** История ходов сброшена на 1951 год (1/4). Реестр пользователей и каналов в безопасности.", parseMode: ParseMode.Markdown, cancellationToken: ct);
        }
        else
        {
            _wipeRequestedAtUtc = null;
            await _botClient.SendTextMessageAsync(adminChatId, "❌ Время подтверждения истекло. Введите `/wipe_all` заново.", cancellationToken: ct);
        }
    }
}