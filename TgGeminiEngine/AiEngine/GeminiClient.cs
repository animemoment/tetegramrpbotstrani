using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TgGeminiEngine.AiEngine;

public class GeminiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<GeminiClient> _logger;

    // Проверенные боевые текстовые модели Google Gemini API
    private static readonly string[] VerifiedTextModels =
    [
        "gemini-3.6-flash",
        "gemini-3.7-flash",
        "gemini-3.1-pro-preview",
        "gemini-flash-latest",
        "gemini-pro-latest"
    ];

    private List<string> _activeModels = [];

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaxCycles = 2;

    public GeminiClient(HttpClient httpClient, string apiKey, ILogger<GeminiClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task InitializeAvailableModelsAsync()
    {
        try
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await _httpClient.GetAsync(url, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("models", out var modelsElem))
                {
                    var fetched = new List<string>();
                    foreach (var m in modelsElem.EnumerateArray())
                    {
                        string cleanName = (m.GetProperty("name").GetString() ?? "").Replace("models/", "");

                        // Отбираем ТОЛЬКО чистые текстовые Flash и Pro (отсекаем tts, audio, image, vision, nano)
                        bool isTextModel = (cleanName.Contains("flash") || cleanName.Contains("pro")) &&
                                           !cleanName.Contains("audio") &&
                                           !cleanName.Contains("tts") &&
                                           !cleanName.Contains("image") &&
                                           !cleanName.Contains("nano") &&
                                           !cleanName.Contains("banana") &&
                                           !cleanName.Contains("lyria") &&
                                           !cleanName.Contains("deep-research") &&
                                           !cleanName.Contains("2.5"); // 2.5 закрыты Google

                        if (isTextModel)
                        {
                            fetched.Add(cleanName);
                        }
                    }

                    if (fetched.Count > 0)
                    {
                        // Приоритет: 3.6-flash на первом месте как самый стабильный
                        _activeModels = fetched
                            .OrderByDescending(x => x.Equals("gemini-3.6-flash"))
                            .ThenByDescending(x => x.Contains("3.7"))
                            .ThenByDescending(x => x.Contains("flash"))
                            .ToList();

                        _logger.LogInformation("✅ [Gemini] Рабочие текстовые модели ({Count} шт.): {List}", 
                            _activeModels.Count, string.Join(", ", _activeModels));
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Не удалось загрузить список моделей: {Msg}. Использую проверенный список.", ex.Message);
        }

        _activeModels = [.. VerifiedTextModels];
    }

    public async Task<string> GenerateContentWithFallbackAsync(string systemInstruction, string userText, int maxOutputTokens = 4096)
    {
        if (_activeModels.Count == 0)
        {
            await InitializeAvailableModelsAsync();
        }

        List<string> errors = [];

        for (int cycle = 1; cycle <= MaxCycles; cycle++)
        {
            foreach (var model in _activeModels)
            {
                try
                {
                    _logger.LogInformation("⚡ [Gemini] Запрос к [{Model}] (цикл {Cycle}/{Max})...", model, cycle, MaxCycles);
                    using var cts = new CancellationTokenSource(RequestTimeout);
                    return await ExecuteCallAsync(model, systemInstruction, userText, maxOutputTokens, cts.Token);
                }
                catch (SafetyTriggeredException)
                {
                    _logger.LogWarning("🚨 [Gemini Safety] Контент заблокирован встроенным фильтром безопасности Google!");
                    return "SAFETY_BLOCKED";
                }
                catch (GeminiRateLimitException ex)
                {
                    _logger.LogWarning("⏳ [Gemini] Модель {Model} вернула лимит 429. Пауза {Sec}с...", model, ex.RetryAfterSeconds);
                    errors.Add($"[{model}]: RateLimit ({ex.RetryAfterSeconds}с)");
                    try { await Task.Delay(ex.RetryAfterSeconds * 1000); } catch { /* отмена */ }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("⏱️ Модель {Model} превысила лимит {Sec}с. Переход к следующей...", model, RequestTimeout.TotalSeconds);
                    errors.Add($"[{model}]: Таймаут {RequestTimeout.TotalSeconds}с");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("⚠️ Ошибка {Model}: {Msg}. Переход...", model, ex.Message);
                    errors.Add($"[{model}]: {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException("Все модели Gemini недоступны:\n" + string.Join("\n", errors));
    }

    private async Task<string> ExecuteCallAsync(string model, string systemInstruction, string userText, int maxOutputTokens, CancellationToken ct)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";

        // Чистая конфигурация без конфликтующих параметров
        var generationConfig = new
        {
            temperature = 0.2,
            maxOutputTokens
        };

        // Отключение стандартных порогов для военных RP-сценариев
        var safetySettings = new[]
        {
            new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
            new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
            new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
            new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
        };

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemInstruction } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userText } } } },
            generationConfig,
            safetySettings
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, ct);
        
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 429)
            {
                throw new GeminiRateLimitException(TryParseRetryAfter(response));
            }

            // 5xx и 4xx (кроме 429): 4xx ретраить бессмысленно, 5xx уйдёт в следующий цикл fallback
            throw new HttpRequestException($"[HTTP {(int)response.StatusCode}] {json}");
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];

            if (firstCandidate.TryGetProperty("finishReason", out var reasonElem))
            {
                string finishReason = reasonElem.GetString() ?? "";
                if (finishReason.Equals("SAFETY", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SafetyTriggeredException();
                }
            }

            if (firstCandidate.TryGetProperty("content", out var contentElem) &&
                contentElem.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
            {
                string text = parts[0].GetProperty("text").GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new GeminiEmptyResponseException();
                }

                return text;
            }
        }

        throw new GeminiEmptyResponseException();
    }

    private static int TryParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return Math.Max(1, (int)Math.Ceiling(diff.TotalSeconds));
        }

        return 5;
    }
}

public class SafetyTriggeredException : Exception { }

public class GeminiRateLimitException : Exception
{
    public int RetryAfterSeconds { get; }

    public GeminiRateLimitException(int retryAfterSeconds)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}

public class GeminiEmptyResponseException : Exception { }