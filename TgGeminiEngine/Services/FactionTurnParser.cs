using System.Text.Json;
using System.Text.RegularExpressions;
using TgGeminiEngine.Domain;

namespace TgGeminiEngine.Services;

// Разбор ответа Gemini: отделение вердикта от паспорта и JSON-блока метрик
public static class FactionTurnParser
{
    public static ParsedTurnResult Parse(string aiResponse, string previousPassport)
    {
        string summary = aiResponse;
        string passport = previousPassport;

        // 1. Паспорт (маркер из промпта SystemEnginePrompt)
        if (aiResponse.Contains("===NEW_PASSPORT==="))
        {
            var parts = aiResponse.Split("===NEW_PASSPORT===", StringSplitOptions.TrimEntries);
            summary = parts[0];
            passport = parts.Length > 1 ? parts[1] : previousPassport;
        }

        // 2. Структурированные метрики (JSON между маркерами)
        FactionMetrics? metrics = null;
        int start = aiResponse.IndexOf("===METRICS_START===", StringComparison.Ordinal);
        int end = aiResponse.IndexOf("===METRICS_END===", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            int markerLen = "===METRICS_START===".Length;
            string json = aiResponse.Substring(start + markerLen, end - start - markerLen);
            metrics = TryParseMetrics(json);
            summary = summary
                .Replace("===METRICS_START===", "")
                .Replace("===METRICS_END===", "")
                .Trim();
        }

        // 3. Failsafe: если JSON не распарсился — извлекаем числа из паспорта
        metrics ??= ExtractMetricsFromPassport(passport);

        return new ParsedTurnResult(summary.Trim(), passport, metrics);
    }

    private static FactionMetrics? TryParseMetrics(string json)
    {
        try
        {
            json = json.Trim();
            if (json.StartsWith("```"))
            {
                int firstNewline = json.IndexOf('\n');
                if (firstNewline >= 0) json = json[(firstNewline + 1)..];
                json = json.Replace("```", "").Trim();
            }

            var metrics = JsonSerializer.Deserialize<FactionMetrics>(json);
            return metrics;
        }
        catch
        {
            return null;
        }
    }

    // Запасной вариант: вытаскиваем ключевые числа прямо из текста паспорта
    private static FactionMetrics? ExtractMetricsFromPassport(string passport)
    {
        if (string.IsNullOrWhiteSpace(passport)) return null;

        var m = new FactionMetrics();
        foreach (var raw in passport.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Contains("ВВП"))
            {
                m.Gdp = ExtractFirstNumber(line);
                int slash = line.IndexOf('/');
                if (slash >= 0) m.Treasury = ExtractFirstNumber(line[(slash + 1)..]);
            }
            else if (line.Contains("Нефть")) m.Oil = ExtractFirstNumber(line);
            else if (line.Contains("Сталь")) m.Steel = ExtractFirstNumber(line);
            else if (line.Contains("Уголь")) m.Coal = ExtractFirstNumber(line);
            else if (line.Contains("Население")) m.Population = ExtractFirstNumber(line);
            else if (line.Contains("Сухопутные")) m.Army = ExtractFirstNumber(line);
            else if (line.Contains("Бронетехника")) m.Tanks = ExtractFirstNumber(line);
            else if (line.Contains("Артиллерия")) m.Artillery = ExtractFirstNumber(line);
            else if (line.Contains("ВВС / ВМФ")) m.Planes = ExtractFirstNumber(line);
        }
        return m;
    }

    private static double? ExtractFirstNumber(string text)
    {
        var match = Regex.Match(text, @"[\d][\d\s.,]*");
        if (!match.Success) return null;

        string cleaned = match.Value.Replace(" ", "").Replace(",", ".");
        return double.TryParse(cleaned, out double v) ? v : null;
    }
}