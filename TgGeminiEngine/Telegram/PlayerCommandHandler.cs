using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgGeminiEngine.AiEngine;
using TgGeminiEngine.Domain;
using TgGeminiEngine.Infrastructure;

namespace TgGeminiEngine.Telegram;

// Команды игроков: регистрация, привязка канала, паспорт, справка
public class PlayerCommandHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly FactionRepository _factionRepo;
    private readonly ChannelPostRepository _postRepo;
    private readonly ChannelScanner _scanner;
    private readonly VerdictDispatcher _dispatcher;

    public PlayerCommandHandler(
        ITelegramBotClient botClient,
        FactionRepository factionRepo,
        ChannelPostRepository postRepo,
        ChannelScanner scanner,
        VerdictDispatcher dispatcher)
    {
        _botClient = botClient;
        _factionRepo = factionRepo;
        _postRepo = postRepo;
        _scanner = scanner;
        _dispatcher = dispatcher;
    }

    // 🎖️ Команда /start (и /reset): Регистрация игрока
    public async Task HandleStartAsync(long userId, string senderUsername, CancellationToken ct)
    {
        await _factionRepo.RecordUserInteractionAsync(userId, senderUsername, canReceiveDm: true);
        await _factionRepo.SavePassportAsync(userId, Prompts.DefaultPassport);
        await _botClient.SendTextMessageAsync(userId,
            "🎖️ **Greenwell RP Engine (1951 год)**\n\n" +
            "Ваш аккаунт подтвержден. Вы будете получать персональные вердикты Генерального Штаба.\n\n" +
            "Привяжите канал своей фракции:\n`/post @имя_канала`",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    // 📥 Команда /post: Привязка канала фракции и синхронизация постов
    public async Task HandleBindAndSyncCommandAsync(long userId, string messageText, CancellationToken ct)
    {
        var parts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long channelId = 0;
        string? channelTitle = null;
        string? channelUsername = null;

        if (parts.Length > 1)
        {
            string target = parts[1];
            if (long.TryParse(target, out long parsed))
            {
                channelId = parsed;
            }
            else
            {
                try
                {
                    var chat = await _botClient.GetChatAsync(target, cancellationToken: ct);
                    channelId = chat.Id;
                    channelTitle = chat.Title;
                    channelUsername = chat.Username;
                }
                catch (Exception ex)
                {
                    await _botClient.SendTextMessageAsync(userId, $"❌ Канал `{target}` не найден. Убедитесь, что бот добавлен туда администратором.\nОшибка: {ex.Message}", parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
            }

            await _postRepo.RegisterKnownChannelAsync(channelId, channelTitle, channelUsername, userId, null, true);
            await _factionRepo.SaveBoundChannelAsync(userId, channelId);
        }
        else
        {
            channelId = await _factionRepo.GetBoundChannelAsync(userId);
            if (channelId == 0)
            {
                await _botClient.SendTextMessageAsync(userId, "ℹ️ Укажите канал: `/post @имя_канала`", parseMode: ParseMode.Markdown, cancellationToken: ct);
                return;
            }
        }

        await _botClient.SendTextMessageAsync(userId, $"📥 Синхронизирую посты из канала `{channelId}`...", parseMode: ParseMode.Markdown, cancellationToken: ct);
        int collected = await _scanner.RobustCollectHistoryAsync(userId, channelId, ct);
        var pending = await _postRepo.GetUnprocessedPostsAsync(channelId);

        await _botClient.SendTextMessageAsync(userId,
            $"✅ **Канал успешно привязан!**\n\n" +
            $"• Новых постов собрано: **{(collected >= 0 ? collected : 0)}**\n" +
            $"• В очереди на расчет: **{pending.Count}**\n\n" +
            $"⏳ *Итоги будут доставлены в ЛС и продублированы в {GameConstants.WorldGazetteChannel}.*",
            parseMode: ParseMode.Markdown,
            cancellationToken: ct);
    }

    // 📜 Команда /passport: Текущий паспорт государства
    public async Task HandlePassportAsync(long userId, CancellationToken ct)
    {
        string passport = await _factionRepo.GetPassportAsync(userId);
        await _dispatcher.SendLongMessageToChatIdAsync(userId, $"📜 **Паспорт Государства:**\n\n{passport}", ct);
    }

    // 📖 Команда /help: Справка по командам
    public async Task HandleHelpAsync(long userId, bool isAdmin, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📖 **Справка по командам:**\n");
        sb.AppendLine("• `/start` — зарегистрироваться для получения ЛС");
        sb.AppendLine("• `/post @канал` — привязать канал фракции и синхронизировать посты");
        sb.AppendLine("• `/passport` — текущий паспорт государства\n");

        if (isAdmin)
        {
            sb.AppendLine($"👑 **Панель Администратора (@{GameConstants.AdminUsername}):**");
            sb.AppendLine("• `/users` — онлайн-тест доступности ЛС у всех владельцев");
            sb.AppendLine("• `/channels` — реестр каналов и очередь постов");
            sb.AppendLine("• `/scan_all` — параллельный сбор постов");
            sb.AppendLine("• `/turn` — расчет хода (ЛС + дублирование в канал при блокировке)");
            sb.AppendLine("• `/test [N]` — сухой прогон хода без записи в БД (посты → @testbotchannel1000)");
            sb.AppendLine("• `/year_report [год]` — персональные годовые отчёты игрокам");
            sb.AppendLine("• `/wipe_all` — сброс постов и паспортов на 1951 год");
        }

        await _botClient.SendTextMessageAsync(userId, sb.ToString(), parseMode: ParseMode.Markdown, cancellationToken: ct);
    }
}