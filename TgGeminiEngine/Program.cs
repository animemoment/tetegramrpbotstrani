using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TgGeminiEngine.AiEngine;
using TgGeminiEngine.Infrastructure;
using TgGeminiEngine.Services;
using TgGeminiEngine.Telegram;

// Секреты: приоритет — переменные окружения TELEGRAM_BOT_TOKEN / GEMINI_API_KEY,
// при их отсутствии используются встроенные значения (токены НЕ удалять!)
string telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "8759820773:AAG9BK-42FqlY7_-K8-ECR2AaOS1SHmlIfI";
string geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "AQ.Ab8RN6JaAESfb0YPN3HC0xe-I863mGkBU2a1hQ1s_WQwpLdGhw";
string proxyUrl = Environment.GetEnvironmentVariable("PROXY_URL") ?? string.Empty;
string dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "states.db";
string connectionString = $"Data Source={dbPath};";

if (string.IsNullOrWhiteSpace(telegramToken))
{
    Console.Error.WriteLine("❌ Не задана переменная окружения TELEGRAM_BOT_TOKEN. Задайте её и перезапустите бота.");
    return 1;
}

if (string.IsNullOrWhiteSpace(geminiApiKey))
{
    Console.Error.WriteLine("❌ Не задана переменная окружения GEMINI_API_KEY. Задайте её и перезапустите бота.");
    return 1;
}

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(new DatabaseInitializer(connectionString));
        services.AddSingleton(new ChannelPostRepository(connectionString));
        services.AddSingleton(new FactionRepository(connectionString));
        services.AddSingleton(new FactionMetricsRepository(connectionString));

        services.AddHttpClient("GeminiClient")
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler();
                if (!string.IsNullOrWhiteSpace(proxyUrl))
                {
                    handler.Proxy = new WebProxy(proxyUrl);
                    handler.UseProxy = true;
                }
                return handler;
            })
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        services.AddHttpClient("TelegramBotClient")
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler();
                if (!string.IsNullOrWhiteSpace(proxyUrl))
                {
                    handler.Proxy = new WebProxy(proxyUrl);
                    handler.UseProxy = true;
                }
                return handler;
            })
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

        services.AddSingleton(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("GeminiClient");
            var logger = sp.GetRequiredService<ILogger<GeminiClient>>();
            return new GeminiClient(httpClient, geminiApiKey, logger);
        });

        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("TelegramBotClient");
            return new TelegramBotClient(telegramToken, httpClient);
        });

        services.AddSingleton<GameResolutionService>();
        services.AddSingleton<YearReportService>();
        services.AddSingleton<ChannelScanner>();
        services.AddSingleton<VerdictDispatcher>();
        services.AddSingleton<AdminCommandHandler>();
        services.AddSingleton<PlayerCommandHandler>();

        services.AddHostedService<TelegramBotWorker>();
    })
    .Build();

var dbInitializer = host.Services.GetRequiredService<DatabaseInitializer>();
await dbInitializer.InitializeAsync();

// Авто-очистка старых обработанных постов (защита от раздувания БД)
var postRepo = host.Services.GetRequiredService<ChannelPostRepository>();
int cleaned = await postRepo.DeleteProcessedPostsOlderThanAsync(DateTime.UtcNow.AddDays(-30));
if (cleaned > 0)
{
    var startLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startLogger.LogInformation("🧹 Авто-очистка: удалено {Count} старых обработанных постов.", cleaned);
}

var geminiClient = host.Services.GetRequiredService<GeminiClient>();
await geminiClient.InitializeAvailableModelsAsync();

await host.RunAsync();
return 0;