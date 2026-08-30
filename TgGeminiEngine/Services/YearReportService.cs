using System.Text;
using Microsoft.Extensions.Logging;
using TgGeminiEngine.AiEngine;
using TgGeminiEngine.Domain;
using TgGeminiEngine.Infrastructure;

namespace TgGeminiEngine.Services;

// Формирование персональных годовых отчётов для игроков по сохранённым метрикам
public class YearReportService
{
    private readonly FactionMetricsRepository _metricsRepo;
    private readonly FactionRepository _factionRepo;
    private readonly GeminiClient _geminiClient;
    private readonly ILogger<YearReportService> _logger;

    public YearReportService(
        FactionMetricsRepository metricsRepo,
        FactionRepository factionRepo,
        GeminiClient geminiClient,
        ILogger<YearReportService> logger)
    {
        _metricsRepo = metricsRepo;
        _factionRepo = factionRepo;
        _geminiClient = geminiClient;
        _logger = logger;
    }

    // Строит годовой отчёт для каждой фракции, у которой есть метрики за указанный год.
    // Возвращает (UserId, ChannelId, ReportText).
    public async Task<List<(long UserId, long ChannelId, string ReportText)>> BuildYearReportsAsync(int year, CancellationToken ct)
    {
        var allMetrics = await _metricsRepo.GetAllMetricsForYearAsync(year);
        var reports = new List<(long, long, string)>();

        foreach (var group in allMetrics.GroupBy(m => m.UserId))
        {
            long userId = group.Key;
            long channelId = await _factionRepo.GetBoundChannelAsync(userId);

            var quarterlyData = new StringBuilder();
            foreach (var q in group.OrderBy(q => q.Quarter))
            {
                quarterlyData.AppendLine($"--- Q{q.Quarter}/{year} ---");
                quarterlyData.AppendLine($"Итог хода: {q.Summary}");
                quarterlyData.AppendLine($"Метрики: {GameResolutionService.FormatMetricsForPrompt(q.Metrics)}");
                quarterlyData.AppendLine();
            }

            try
            {
                string factionName = $"Государство (канал {channelId})";
                string prompt = Prompts.BuildYearReportPrompt(factionName, year, quarterlyData.ToString());
                string aiResponse = await _geminiClient.GenerateContentWithFallbackAsync(
                    "Ты — Главный архивариус и стратег бюллетеня World Gazette (вымышленный мир 1950-х).",
                    prompt,
                    maxOutputTokens: 12288
                );

                string reportText = $"📅 **ГОДОВОЙ ОТЧЁТ ({year} ГОД)**\n\n{aiResponse}";
                reports.Add((userId, channelId, reportText));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка формирования годового отчёта для {UserId} за {Year}", userId, year);
            }
        }

        return reports;
    }
}