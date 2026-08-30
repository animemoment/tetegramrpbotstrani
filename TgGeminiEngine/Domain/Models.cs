namespace TgGeminiEngine.Domain;

public record ChannelPostRecord(
    long ChannelId,
    int MessageId,
    DateTime PostDate,
    string Content,
    bool IsProcessed
);

public record KnownChannelRecord(
    long ChannelId, 
    string Title, 
    string Username, 
    long OwnerId, 
    string OwnerUsername, 
    bool IsActive
);

public record FactionStateRecord(
    long UserId,
    string Passport,
    long BoundChannelId
);

public record ValidationResult(bool IsValid, string Reason = "");

// Структурированные метрики государства, которые модель дублирует в JSON-блоке ответа
public class FactionMetrics
{
    public double? Gdp { get; set; }
    public double? Treasury { get; set; }
    public double? TradeBalance { get; set; }
    public double? Oil { get; set; }
    public double? Steel { get; set; }
    public double? Coal { get; set; }
    public double? Population { get; set; }
    public double? Army { get; set; }
    public double? Tanks { get; set; }
    public double? Artillery { get; set; }
    public double? Planes { get; set; }
    public double? Ships { get; set; }
    public double? Instability { get; set; }
    public double? RebellionRisk { get; set; }
    public List<string> Wars { get; set; } = [];
    public List<string> Allies { get; set; } = [];
    public List<string> Enemies { get; set; } = [];
    public List<string> Treaties { get; set; } = [];
}

// Результат разбора ответа Gemini за ход государства
public record ParsedTurnResult(
    string Summary,
    string Passport,
    FactionMetrics? Metrics
);

// Запись метрик одного квартала одной фракции (для годового отчёта)
public record QuarterMetricsRecord(
    int Year,
    int Quarter,
    FactionMetrics? Metrics,
    string Summary
);

public record YearMetricsRecord(
    long UserId,
    int Year,
    int Quarter,
    FactionMetrics? Metrics,
    string Summary
);

// Пост после категоризации для передачи модели (с RP-датой и корзиной)
public record CategorizedPostRecord(
    ChannelPostRecord Post,
    string Category,
    string RpDateLabel
);