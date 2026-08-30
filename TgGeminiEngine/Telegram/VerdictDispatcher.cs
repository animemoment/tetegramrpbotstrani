using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TgGeminiEngine.Telegram;

// Доставка длинных сообщений: разбиение на чанки с защитой Markdown-форматирования
public class VerdictDispatcher
{
    private readonly ITelegramBotClient _botClient;

    public VerdictDispatcher(ITelegramBotClient botClient)
    {
        _botClient = botClient;
    }

    public async Task SendLongMessageToChatIdAsync(long chatId, string text, CancellationToken ct)
    {
        var chunks = SplitIntoChunks(text, 3800);
        foreach (var chunk in chunks)
        {
            try
            {
                await _botClient.SendTextMessageAsync(chatId, chunk, parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
            catch
            {
                // Markdown не распарсился — отправляем plain-текст без Markdown-мусора
                await _botClient.SendTextMessageAsync(chatId, CleanMarkdownForPlainText(chunk), cancellationToken: ct);
            }
            await Task.Delay(80, ct);
        }
    }

    public async Task SendLongMessageAsync(string channelUsername, string text, CancellationToken ct)
    {
        var chunks = SplitIntoChunks(text, 3800);
        foreach (var chunk in chunks)
        {
            try
            {
                await _botClient.SendTextMessageAsync(channelUsername, chunk, parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
            catch
            {
                await _botClient.SendTextMessageAsync(channelUsername, CleanMarkdownForPlainText(chunk), cancellationToken: ct);
            }
            await Task.Delay(80, ct);
        }
    }

    private static List<string> SplitIntoChunks(string text, int maxChunkSize)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        var lines = text.Split('\n');
        var currentChunk = new StringBuilder();

        foreach (var line in lines)
        {
            if (currentChunk.Length + line.Length + 1 > maxChunkSize)
            {
                if (currentChunk.Length > 0)
                {
                    chunks.Add(CloseMarkdownIfNeeded(currentChunk.ToString()));
                    currentChunk.Clear();
                }

                if (line.Length > maxChunkSize)
                {
                    for (int i = 0; i < line.Length; i += maxChunkSize)
                    {
                        chunks.Add(line.Substring(i, Math.Min(maxChunkSize, line.Length - i)));
                    }
                    continue;
                }
            }

            if (currentChunk.Length > 0)
                currentChunk.AppendLine();
            currentChunk.Append(line);
        }

        if (currentChunk.Length > 0)
            chunks.Add(CloseMarkdownIfNeeded(currentChunk.ToString()));

        return chunks;
    }

    // Если чанк обрезан посреди **жирного** — закрываем маркер, чтобы Telegram не разорвал форматирование
    private static string CloseMarkdownIfNeeded(string chunk)
    {
        if (CountMarkdownBold(chunk) % 2 != 0)
            return chunk + "**";

        return chunk;
    }

    private static int CountMarkdownBold(string text)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf("**", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += 2;
        }
        return count;
    }

    // Удаляет Markdown-мусор (жирный, подчёркивания, backticks, заголовки) для отправки plain-текстом
    // при сбое парсинга Markdown в Telegram
    public static string CleanMarkdownForPlainText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        return text
            .Replace("**", "")
            .Replace("__", "")
            .Replace("## ", "")
            .Replace("### ", "")
            .Replace("`", "");
    }
}