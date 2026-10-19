using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Qualidade dos dados e confiança da previsão, calculadas sobre as oportunidades
// abertas. Mostradas em Configurações (saíram do cabeçalho da Visão Executiva).
// ---------------------------------------------------------------------------
public static class SaudeDaPrevisao
{
    public sealed record Resultado(int Abertas, int Qualidade, int Confianca, int Precisao, int Disciplina);

    public static Resultado Calcular(IEnumerable<Opportunity> todas, DateTime hoje)
    {
        var open = todas.Where(o => ForecastCategories.IsOpen(o.Category)).ToList();
        var dq = (int)Math.Round(open.Count == 0 ? 100 : open.Average(o => Qualidade(o, hoje)));

        var withMgr = open.Where(o => o.ManagerProbabilityValue is not null).ToList();
        var biasAbs = withMgr.Count > 0 ? withMgr.Average(o => Math.Abs(o.WinProbabilityValue - o.ManagerProbabilityValue!.Value)) : 8;
        var precisao = (int)Math.Clamp(95 - biasAbs, 50, 96);
        var disciplina = open.Count > 0
            ? (int)Math.Round((double)open.Count(o => ForecastCalc.DaysSinceUpdate(o, hoje) <= 21) / open.Count * 100)
            : 100;
        return new Resultado(open.Count, dq, (precisao + disciplina) / 2, precisao, disciplina);
    }

    // Oito itens de cadastro, 12,5 pontos cada.
    private static double Qualidade(Opportunity o, DateTime hoje)
    {
        double s = 0; const double each = 100.0 / 8;
        if (!string.IsNullOrWhiteSpace(o.CustomerId)) s += each;
        if (!string.IsNullOrWhiteSpace(o.ProductId)) s += each;
        if (o.ExpectedDateValue is not null) s += each;
        if (o.AmountUsd > 0) s += each;
        if (o.WinProbabilityValue is > 0 and <= 100) s += each;
        if (!string.IsNullOrWhiteSpace(o.NextAction)) s += each;
        if (ForecastCalc.DaysSinceUpdate(o, hoje) <= 21) s += each;
        if (!string.IsNullOrWhiteSpace(o.PipelineStageId)) s += each;
        return s;
    }

    public static string Css(int v) => v >= 85 ? "st-pos" : v >= 70 ? "st-warn" : "st-neg";
}
