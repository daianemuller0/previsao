using System.Globalization;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Tradução para exibição em inglês (tabela de oportunidades, painel de colunas,
// estúdio de gráficos). As definições compartilhadas (OpportunityColumns,
// AlteracoesCrm, PendenciasPrevisao) continuam em português e com as mesmas
// chaves: aqui só se traduz o texto mostrado, pelo rótulo em português.
// ---------------------------------------------------------------------------
public static class RotulosEn
{
    public static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private static readonly Dictionary<string, string> Rotulos = new(StringComparer.Ordinal)
    {
        ["Setor"] = "Sector",
        ["País"] = "Country",
        ["Market Variável"] = "Sub-market",
        ["Proposta"] = "Proposal",
        ["% de Ganho"] = "Win %",
        ["% de Sair no Mês"] = "% Closing in Month",
        ["Chance Conversão"] = "Conversion Likelihood",
        ["Serviço previsto"] = "Planned Service",
        ["Unidade de Venda"] = "Sales Unit",
        ["Observação"] = "Notes",
        ["VALOR USD"] = "USD VALUE",
        ["Taxa"] = "Rate",
        ["BU de origem"] = "Source BU",
        ["CRM de origem"] = "Source CRM",
        ["Moeda de origem"] = "Source Currency",
        ["Valor de origem"] = "Source Value",
        ["Data prevista"] = "Expected date",
        ["Nome"] = "Name",
        ["Cliente"] = "Customer",
        ["Próxima ação"] = "Next action",
        ["Riscos"] = "Risks",
        ["Etapa do funil"] = "Funnel stage",
        ["Indicada na Previsão"] = "Referred to Forecast",
        ["Movida para o Controle"] = "Moved to Control",
        ["Perdida"] = "Lost",
    };

    /// <summary>Rótulo em inglês de um rótulo em português; o próprio texto se não houver tradução.</summary>
    public static string Rotulo(string pt) => Rotulos.TryGetValue(pt, out var en) ? en : pt;

    // Mesma lista de AlteracoesCrm (campos numéricos).
    private static readonly HashSet<string> Numericos = new(StringComparer.Ordinal)
    {
        nameof(Opportunity.AmountOriginal), nameof(Opportunity.WinProbability),
        nameof(Opportunity.CloseInPeriodProbability), nameof(Opportunity.GmPercent),
    };

    private static double Num(string? s)
    {
        double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v);
        return v;
    }

    public static string Data(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("MM/dd/yyyy", EnUs) : iso;
    }

    /// <summary>Equivalente em inglês de AlteracoesCrm.Mostrar.</summary>
    public static string CrmMostrar(string campo, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return "—";
        if (campo == nameof(Opportunity.ExpectedDate)) return Data(valor);
        if (campo == nameof(Opportunity.AmountOriginal)) return Num(valor).ToString("#,##0.##", EnUs);
        if (Numericos.Contains(campo)) return Num(valor).ToString("0.##", EnUs) + "%";
        return valor.Trim();
    }

    /// <summary>Equivalente em inglês de AlteracoesCrm.QuemQuando.</summary>
    public static string CrmQuemQuando(Opportunity o, string campo)
    {
        var quando = AlteracoesCrm.Quando(o, campo);
        var quem = AlteracoesCrm.Quem(o);
        var s = "";
        if (quem != "") s += " by " + quem;
        if (!string.IsNullOrWhiteSpace(quando)) s += " on " + Data(quando);
        return s;
    }

    /// <summary>Equivalente em inglês de AlteracoesCrm.Descricao (null quando o campo não foi alterado).</summary>
    public static string? CrmDescricao(Opportunity o, string campo)
    {
        if (AlteracoesCrm.Descricao(o, campo) is null) return null;
        var crm = AlteracoesCrm.ValorCrm(o, campo);
        return $"Changed in the system{CrmQuemQuando(o, campo)} · In CRM: {CrmMostrar(campo, crm)} · Here: {CrmMostrar(campo, AlteracoesCrm.Valor(o, campo))}";
    }

    /// <summary>Número compacto em inglês (2.4M · 850K · 1.06B · 500).</summary>
    public static string Compact(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1_000_000_000) return (v / 1_000_000_000).ToString("0.##", EnUs) + "B";
        if (a >= 1_000_000) return (v / 1_000_000).ToString("0.0", EnUs) + "M";
        if (a >= 1_000) return Math.Round(v / 1_000).ToString("#,##0", EnUs) + "K";
        return v.ToString("#,##0", EnUs);
    }

    public static string MonthShort(int m) => m is >= 1 and <= 12
        ? EnUs.DateTimeFormat.GetAbbreviatedMonthName(m).ToUpperInvariant()
        : "—";

    public static string MonthLong(int m) => m is >= 1 and <= 12 ? EnUs.DateTimeFormat.GetMonthName(m) : "—";
}
