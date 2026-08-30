using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using TgGeminiEngine.Domain;
using TgGeminiEngine.Infrastructure;

namespace TgGeminiEngine.Telegram;

// Сканирование каналов: определение владельца, проверка ЛС, сбор истории постов
public class ChannelScanner
{
    private readonly ITelegramBotClient _botClient;
    private readonly ChannelPostRepository _postRepo;
    private readonly FactionRepository _factionRepo;
    private readonly ILogger<ChannelScanner> _logger;

    public ChannelScanner(
        ITelegramBotClient botClient,
        ChannelPostRepository postRepo,
        FactionRepository factionRepo,
        ILogger<ChannelScanner> logger)
    {
        _botClient = botClient;
        _postRepo = postRepo;
        _factionRepo = factionRepo;
        _logger = logger;
    }

    // 🕵️ Определение Создателя / Главного Администратора канала
    public async Task<(long OwnerId, string OwnerUsername)> TryDetectChannelOwnerAsync(long channelId, CancellationToken ct)
    {
        try
        {
            var admins = await _botClient.GetChatAdministratorsAsync(channelId, ct);
            var creator = admins.FirstOrDefault(a => a.Status == ChatMemberStatus.Creator);
            if (creator != null && !creator.User.IsBot)
            {
                return (creator.User.Id, creator.User.Username ?? creator.User.FirstName);
            }

            var humanAdmin = admins.FirstOrDefault(a => !a.User.IsBot);
            if (humanAdmin != null)
            {
                return (humanAdmin.User.Id, humanAdmin.User.Username ?? humanAdmin.User.FirstName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Не удалось определить владельца канала {ChannelId}: {Msg}", channelId, ex.Message);
        }

        return (0, string.Empty);
    }

    // 🕵️ Бесшумная проверка возможности писать юзеру в ЛС без отправки сообщений
    public async Task<bool> CheckIfUserCanReceiveDmAsync(long userId, CancellationToken ct)
    {
        if (userId <= 0) return false;
        try
        {
            // Отправка Typing не отображает текстовых сообщений, но проверяет статус диалога в Telegram
            await _botClient.SendChatActionAsync(userId, ChatAction.Typing, cancellationToken: ct);
            await _factionRepo.RecordUserInteractionAsync(userId, null, canReceiveDm: true);
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403 || ex.Message.Contains("chat not found") || ex.Message.Contains("can't initiate"))
        {
            await _factionRepo.RecordUserInteractionAsync(userId, null, canReceiveDm: false);
            return false;
        }
        catch
        {
            return false;
        }
    }

    // 📥 Сбор истории постов канала с защитой от 429 и пропуска сообщений
    public async Task<int> RobustCollectHistoryAsync(long stagingChatId, long channelId, CancellationToken ct)
    {
        int topId = 0;

        try
        {
            var ping = await _botClient.SendTextMessageAsync(channelId, "⏳ Синхронизация базы...", cancellationToken: ct);
            topId = ping.MessageId;
            try { await _botClient.DeleteMessageAsync(channelId, topId, ct); } catch { }
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403 || ex.Message.Contains("not a member") || ex.Message.Contains("Forbidden"))
        {
            return -1;
        }
        catch
        {
            topId = 300;
        }

        if (topId <= 1) return 0;

        int lastSaved = await _postRepo.GetLastKnownMessageIdAsync(channelId);
        int collectedCount = 0;
        int emptyGapStreak = 0;
        int rateLimitStreak = 0;
        const int maxGapStreak = 30;
        const int maxRateLimitStreak = 10;

        for (int currentId = topId - 1; currentId >= 1; currentId--)
        {
            if (ct.IsCancellationRequested) break;
            if (currentId <= lastSaved && lastSaved > 0) break;

            try
            {
                var fwd = await _botClient.ForwardMessageAsync(stagingChatId, channelId, currentId, cancellationToken: ct);
                string text = fwd.Text ?? fwd.Caption ?? string.Empty;
                DateTime postDate = (fwd.ForwardDate ?? fwd.Date).ToUniversalTime();

                try { await _botClient.DeleteMessageAsync(stagingChatId, fwd.MessageId, ct); } catch { }

                if (postDate < GameConstants.RpStartDateUtc) break;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    await _postRepo.SavePostAsync(channelId, currentId, postDate, text);
                    collectedCount++;
                }

                emptyGapStreak = 0;
                rateLimitStreak = 0;
                await Task.Delay(35, ct);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                int waitSec = ex.Parameters?.RetryAfter ?? 3;
                await Task.Delay(waitSec * 1000, ct);
                rateLimitStreak++;
                if (rateLimitStreak >= maxRateLimitStreak) break;
                // ВАЖНО: currentId НЕ увеличиваем — иначе пропустим реальное сообщение канала
            }
            catch
            {
                emptyGapStreak++;
                if (emptyGapStreak >= maxGapStreak) break;
            }
        }

        return collectedCount;
    }
}