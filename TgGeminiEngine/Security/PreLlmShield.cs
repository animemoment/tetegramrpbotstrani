using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using TgGeminiEngine.Domain;

namespace TgGeminiEngine.Security;

public static class PreLlmShield
{
    private static readonly Regex SmashRegex = new(@"([а-яa-z\d])\1{5,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ValidationResult ValidatePost(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 10)
            return new ValidationResult(false, "Слишком короткий текст или пустое сообщение.");

        if (SmashRegex.IsMatch(text))
            return new ValidationResult(false, "Обнаружен спам повторяющимися символами.");

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 200)
        {
            using var ms = new MemoryStream();
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }
            double ratio = (double)ms.ToArray().Length / bytes.Length;
            
            if (ratio < 0.08)
                return new ValidationResult(false, "Аномально низкая энтропия (зацикленный спам).");
        }

        return new ValidationResult(true);
    }
}

// 🛡️ Защита от спама командами (Кулдаун на пользователя)
public static class CommandThrottler
{
    private static readonly ConcurrentDictionary<long, DateTime> LastCommands = new();
    private static readonly TimeSpan MaxRecordAge = TimeSpan.FromHours(1);

    public static bool IsThrottled(long userId, TimeSpan cooldown)
    {
        var now = DateTime.UtcNow;
        if (LastCommands.TryGetValue(userId, out var lastTime))
        {
            if (now - lastTime < cooldown)
            {
                return true;
            }
        }
        LastCommands[userId] = now;
        CleanupIfNeeded(now);
        return false;
    }

    // Анти-утечка памяти: периодически вычищаем устаревшие записи
    private static void CleanupIfNeeded(DateTime now)
    {
        if (LastCommands.Count < 1000) return;

        foreach (var pair in LastCommands)
        {
            if (now - pair.Value > MaxRecordAge)
            {
                LastCommands.TryRemove(pair.Key, out _);
            }
        }
    }
}