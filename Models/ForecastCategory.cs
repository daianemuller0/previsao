namespace HowdenSalesForecast.Models;

// Categorias de forecast (Section 9). Ordem = maturidade/confiança comercial.
public enum ForecastCategory
{
    Commit,
    BestCase,
    Pipeline,
    Upside,
    Risk,
    ClosedWon,
    ClosedLost,
    Postponed,
}

public static class ForecastCategories
{
    // Rótulo de exibição (mantém o vocabulário corporativo em inglês).
    public static string Label(ForecastCategory c) => c switch
    {
        ForecastCategory.Commit => "Commit",
        ForecastCategory.BestCase => "Best Case",
        ForecastCategory.Pipeline => "Pipeline",
        ForecastCategory.Upside => "Upside",
        ForecastCategory.Risk => "Risk",
        ForecastCategory.ClosedWon => "Closed Won",
        ForecastCategory.ClosedLost => "Closed Lost",
        ForecastCategory.Postponed => "Postponed",
        _ => c.ToString(),
    };

    // Classe CSS do badge (cores discretas e corporativas).
    public static string Css(ForecastCategory c) => c switch
    {
        ForecastCategory.Commit => "fc-commit",
        ForecastCategory.BestCase => "fc-best",
        ForecastCategory.Pipeline => "fc-pipe",
        ForecastCategory.Upside => "fc-upside",
        ForecastCategory.Risk => "fc-risk",
        ForecastCategory.ClosedWon => "fc-won",
        ForecastCategory.ClosedLost => "fc-lost",
        ForecastCategory.Postponed => "fc-post",
        _ => "fc-pipe",
    };

    public static ForecastCategory Parse(string? s) =>
        Enum.TryParse<ForecastCategory>(s, ignoreCase: true, out var c) ? c : ForecastCategory.Pipeline;

    // Estados considerados "abertos" (ainda no funil).
    public static bool IsOpen(ForecastCategory c) =>
        c is not (ForecastCategory.ClosedWon or ForecastCategory.ClosedLost);

    // Categorias elegíveis para compor o forecast do período (comprometido + provável).
    public static bool CountsToForecast(ForecastCategory c) =>
        c is ForecastCategory.Commit or ForecastCategory.BestCase or ForecastCategory.Risk;
}

public enum RiskLevel { Baixo, Moderado, Alto, Critico }

public static class RiskLevels
{
    public static string Label(RiskLevel r) => r switch
    {
        RiskLevel.Baixo => "Baixo",
        RiskLevel.Moderado => "Moderado",
        RiskLevel.Alto => "Alto",
        RiskLevel.Critico => "Crítico",
        _ => r.ToString(),
    };

    public static string Css(RiskLevel r) => r switch
    {
        RiskLevel.Baixo => "rk-low",
        RiskLevel.Moderado => "rk-mod",
        RiskLevel.Alto => "rk-high",
        RiskLevel.Critico => "rk-crit",
        _ => "rk-low",
    };
}
