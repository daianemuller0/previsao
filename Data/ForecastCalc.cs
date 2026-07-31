using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Regras de negócio do forecast (Seção 8 do escopo). Funções puras sobre a
// oportunidade; consolidação em USD, moeda executiva. Sem estado.
// ---------------------------------------------------------------------------
public static class ForecastCalc
{
    // Forecast ponderado = valor × ((% de ganho + % de sair no mês) / 2).
    public static double WeightedUsd(Opportunity o) =>
        o.AmountUsd * (Confidence(o) / 100.0);

    // Confiança da oportunidade = média das duas probabilidades (0–100).
    public static double Confidence(Opportunity o) =>
        (o.WinProbabilityValue + o.CloseProbabilityValue) / 2.0;

    // Faixa de confiança (Muito alta / Alta / Média / Baixa).
    public static string ConfidenceBand(double confidence) =>
        confidence >= 85 ? "Muito alta" : confidence >= 70 ? "Alta" : confidence >= 45 ? "Média" : "Baixa";

    public static string ConfidenceBandCss(double confidence) =>
        confidence >= 85 ? "st-pos" : confidence >= 70 ? "st-info" : confidence >= 45 ? "st-warn" : "st-neutral";

    // Ponderado em BRL (moeda padrão da plataforma).
    public static double WeightedBrl(Opportunity o) =>
        o.AmountBrl * (Confidence(o) / 100.0);

    // Margem bruta prevista = valor × GM%.
    public static double MarginUsd(Opportunity o) => o.AmountUsd * (o.GmPercentValue / 100.0);
    public static double MarginBrl(Opportunity o) => o.AmountBrl * (o.GmPercentValue / 100.0);

    public static int DaysSinceUpdate(Opportunity o, DateTime today)
    {
        var u = o.UpdatedAtValue;
        return u is null ? 999 : Math.Max(0, (int)(today - u.Value.Date).TotalDays);
    }

    public static int DaysToExpected(Opportunity o, DateTime today)
    {
        var d = o.ExpectedDateValue;
        return d is null ? 99999 : (int)Math.Round((d.Value.Date - today).TotalDays);
    }

    // Score de risco 0–100 (maior = mais arriscado). Combina os sinais da Seção 8.
    public static int RiskScore(Opportunity o, DateTime today)
    {
        double s = 0;

        // Probabilidade de ganho baixa (até 30 pts).
        s += (100 - o.WinProbabilityValue) * 0.30;

        // Tempo sem atualização (até 20 pts; satura em 60 dias).
        s += Math.Min(DaysSinceUpdate(o, today), 60) / 60.0 * 20;

        // Postergações (até 15 pts; 3+ satura).
        s += Math.Min(o.PostponeCountValue, 3) / 3.0 * 15;

        // Ausência de próxima ação (10 pts).
        if (string.IsNullOrWhiteSpace(o.NextAction)) s += 10;

        // Margem abaixo do saudável (até 12 pts; <15% = pior).
        if (o.GmPercentValue < 25) s += (25 - Math.Max(o.GmPercentValue, 5)) / 20.0 * 12;

        // Divergência vendedor × gestor (até 8 pts).
        if (o.ManagerProbabilityValue is double mp)
            s += Math.Min(Math.Abs(o.WinProbabilityValue - mp), 40) / 40.0 * 8;

        // Data já vencida sem fechar (5 pts).
        if (DaysToExpected(o, today) < 0 && ForecastCategories.IsOpen(o.Category)) s += 5;

        return (int)Math.Round(Math.Clamp(s, 0, 100));
    }

    public static RiskLevel Level(int score) => score switch
    {
        < 25 => RiskLevel.Baixo,
        < 50 => RiskLevel.Moderado,
        < 75 => RiskLevel.Alto,
        _ => RiskLevel.Critico,
    };

    public static RiskLevel Level(Opportunity o, DateTime today) => Level(RiskScore(o, today));

    // ---- consolidações de carteira ----------------------------------------

    // Pipeline coverage = pipeline elegível ÷ meta restante.
    public static double Coverage(double eligiblePipeline, double remainingTarget) =>
        remainingTarget <= 0 ? 0 : eligiblePipeline / remainingTarget;

    // Gap para a meta = meta − realizado − forecast elegível.
    public static double Gap(double target, double realized, double eligibleForecast) =>
        target - realized - eligibleForecast;

    // Concentração: participação do maior grupo (0–1) sobre um seletor de chave.
    public static double Concentration(IEnumerable<Opportunity> opps, Func<Opportunity, string> key)
    {
        var byKey = opps.GroupBy(key).Select(g => g.Sum(o => o.AmountUsd)).ToList();
        var total = byKey.Sum();
        return total <= 0 ? 0 : byKey.Max() / total;
    }
}
