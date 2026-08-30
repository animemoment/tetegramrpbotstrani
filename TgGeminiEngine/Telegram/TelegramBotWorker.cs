using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgGeminiEngine.Domain;
using TgGeminiEngine.Infrastructure;
using TgGeminiEngine.Security;

namespace TgGeminiEngine.Telegram;

public class TelegramBotWorker : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly FactionRepository _factionRepo;
    private readonly ChannelPostRepository _postRepo;
    private readonly AdminCommandHandler _adminHandler;
    private readonly PlayerCommandHandler _playerHandler;
    private readonly ChannelScanner _scanner;
    private readonly ILogger<TelegramBotWorker> _logger;

    private const string AdminUsername = GameConstants.AdminUsername;
    private const string WorldGazetteChannel = GameConstants.WorldGazetteChannel;
    private static readonly DateTime RpStartDateUtc = GameConstants.RpStartDateUtc;

    // Числовой ID администратора из env (надёжнее username, который можно сменить в Telegram)
    private static readonly long? AdminUserId = ParseAdminUserId();

    private static long? ParseAdminUserId()
    {
        string raw = Environment.GetEnvironmentVariable("ADMIN_USER_ID") ?? string.Empty;
        return long.TryParse(raw, out long id) && id > 0 ? id : null;
    }

    private static bool IsAdmin(long userId, string username)
    {
        if (AdminUserId is { } adminId && userId == adminId)
            return true;

        // Если числовой ID не задан — fallback на username (текущее поведение)
        return AdminUserId is null &&
               username.Equals(AdminUsername, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeavyCommand(string messageText) =>
        messageText == "/turn" || messageText == "/resolve_all" ||
        messageText == "/scan_all" || messageText == "/sync_all" ||
        messageText == "/wipe_all" || messageText == "/wipe_confirm" ||
        messageText.StartsWith("/test") || messageText.StartsWith("/year_report");

    public TelegramBotWorker(
        ITelegramBotClient botClient,
        FactionRepository factionRepo,
        ChannelPostRepository postRepo,
        AdminCommandHandler adminHandler,
        PlayerCommandHandler playerHandler,
        ChannelScanner scanner,
        ILogger<TelegramBotWorker> logger)
    {
        _botClient = botClient;
        _factionRepo = factionRepo;
        _postRepo = postRepo;
        _adminHandler = adminHandler;
        _playerHandler = playerHandler;
        _scanner = scanner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.ChannelPost, UpdateType.MyChatMember],
            ThrowPendingUpdates = true
        };

        _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);

        var me = await _botClient.GetMeAsync(stoppingToken);
        _logger.LogInformation("Бот @{Username} запущен! Админ: @{Admin}, Газета: {Channel}", me.Username, AdminUsername, WorldGazetteChannel);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessUpdateInternalAsync(bot, update, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при фоновой обработке запроса");
            }
        }, ct);

        return Task.CompletedTask;
    }

    private async Task ProcessUpdateInternalAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        // 1. Фиксация активности любого пользователя, который пишет боту
        if (update.Message?.From is { } sender)
        {
            await _factionRepo.RecordUserInteractionAsync(sender.Id, sender.Username ?? sender.FirstName, canReceiveDm: true);
        }

        // 2. Обработка добавления бота в канал
        if (update.MyChatMember is { } myChatMember)
        {
            var chat = myChatMember.Chat;
            bool isMember = myChatMember.NewChatMember.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Member;
            
            long ownerId = myChatMember.From.Id;
            string ownerUsername = myChatMember.From.Username ?? myChatMember.From.FirstName;

            if (isMember)
            {
                var detectedOwner = await _scanner.TryDetectChannelOwnerAsync(chat.Id, ct);
                if (detectedOwner.OwnerId != 0)
                {
                    ownerId = detectedOwner.OwnerId;
                    ownerUsername = detectedOwner.OwnerUsername;
                }
            }

            await _postRepo.RegisterKnownChannelAsync(chat.Id, chat.Title, chat.Username, ownerId, ownerUsername, isMember);
            return;
        }

        // 3. Фоновый перехват постов из каналов
        if (update.ChannelPost is { } post)
        {
            await _postRepo.RegisterKnownChannelAsync(post.Chat.Id, post.Chat.Title, post.Chat.Username, 0, null, true);

            DateTime postDate = post.Date.ToUniversalTime();
            if (postDate >= RpStartDateUtc)
            {
                string content = post.Text ?? post.Caption ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(content))
                {
                    await _postRepo.SavePostAsync(post.Chat.Id, post.MessageId, postDate, content);
                    _logger.LogInformation("📥 [Realtime] Пост #{Id} из канала {ChatId} сохранен.", post.MessageId, post.Chat.Id);
                }
            }
            return;
        }

        if (update.Message is not { Text: { } text } msg) return;
        long userId = msg.Chat.Id;
        string messageText = text.Trim();
        string senderUsername = msg.From?.Username ?? string.Empty;

        // Анти-дубль: кулдаун на тяжёлые команды админа
        if (IsHeavyCommand(messageText) && CommandThrottler.IsThrottled(userId, TimeSpan.FromSeconds(5)))
        {
            await bot.SendTextMessageAsync(userId, "⏳ Команда уже выполняется. Подождите несколько секунд.", cancellationToken: ct);
            return;
        }

        // =====================================================================
        // 👑 ПАНЕЛЬ УПРАВЛЕНИЯ АДМИНИСТРАТОРА
        // =====================================================================

        // 🔍 Реальный онлайн-тест доступности ЛС
        if (messageText == "/users" || messageText == "/sync_users")
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }
            await _adminHandler.HandleSyncAndListUsersCommandAsync(userId, ct);
            return;
        }

        if (messageText == "/list_channels" || messageText == "/channels")
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }
            await _adminHandler.HandleListChannelsCommandAsync(userId, ct);
            return;
        }

        if (messageText == "/scan_all" || messageText == "/sync_all")
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }
            await _adminHandler.HandleScanAllChannelsParallelCommandAsync(userId, ct);
            return;
        }

        if (messageText == "/wipe_all")
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }

            await _adminHandler.RequestWipeAsync(userId, ct);
            return;
        }

        if (messageText == "/wipe_confirm")
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }

            await _adminHandler.ConfirmWipeAsync(userId, ct);
            return;
        }

        if (messageText.StartsWith("/test"))
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }

            int maxPosts = 15;
            var testParts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (testParts.Length > 1 && int.TryParse(testParts[1], out int n) && n > 0 && n <= 100)
                maxPosts = n;

            await _adminHandler.HandleTestCommandAsync(userId, maxPosts, ct);
            return;
        }

        if (messageText.StartsWith("/year_report"))
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }

            int year = GameConstants.BaseYear;
            var yearParts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (yearParts.Length > 1 && int.TryParse(yearParts[1], out int y) && y >= GameConstants.BaseYear && y <= GameConstants.BaseYear + 50)
                year = y;

            await _adminHandler.HandleYearReportCommandAsync(userId, year, ct);
            return;
        }

        if (messageText.StartsWith("/turn") || messageText.StartsWith("/resolve_all"))
        {
            if (!IsAdmin(userId, senderUsername))
            {
                await bot.SendTextMessageAsync(userId, "⛔ Доступ запрещен.", cancellationToken: ct);
                return;
            }
            await _adminHandler.HandleGlobalTurnCommandAsync(userId, ct);
            return;
        }

        // =====================================================================
        // 🎮 КОМАНДЫ ДЛЯ ИГРОКОВ
        // =====================================================================

        if (messageText == "/start" || messageText == "/reset")
        {
            await _playerHandler.HandleStartAsync(userId, senderUsername, ct);
            return;
        }

        if (messageText.StartsWith("/post"))
        {
            await _playerHandler.HandleBindAndSyncCommandAsync(userId, messageText, ct);
            return;
        }

        if (messageText == "/passport")
        {
            await _playerHandler.HandlePassportAsync(userId, ct);
            return;
        }

        if (messageText == "/help")
        {
            bool isAdmin = IsAdmin(userId, senderUsername);
            await _playerHandler.HandleHelpAsync(userId, isAdmin, ct);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Ошибка Telegram API Polling");
        return Task.CompletedTask;
    }
}
