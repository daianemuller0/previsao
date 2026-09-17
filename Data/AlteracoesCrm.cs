using System.Globalization;
using System.Reflection;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// O que foi alterado NO SISTEMA em relação ao que o CRM trouxe.
//
// A sincronização guarda em CrmSnapshot o valor que a planilha trouxe por
// último para cada campo protegido (data, valor, %…). Se o que está gravado é
// diferente daquilo, alguém mexeu aqui — e é isso que esta classe responde,
// campo a campo, com o valor do CRM ao lado para comparar.
//
// Preencher um campo que o CRM trouxe vazio NÃO conta como alteração: não há
// dado do CRM sendo substituído, e marcar isso pintaria de "alterada" toda
// oportunidade indicada (a % de ganho, por exemplo, é sempre do vendedor).
// ---------------------------------------------------------------------------
public static class AlteracoesCrm
{
    public sealed record Campo(string Nome, string Rotulo, string ValorCrm, string ValorSistema);

    // Separadores do CrmSnapshot (fora do teclado: observação pode ter ";" e "=").
    public const char SepCampo = '', SepValor = '';

    // Mesma lista da sincronização (DataSyncService.CamposProtegidos).
    public static readonly string[] Protegidos =
    {
        nameof(Opportunity.ExpectedDate),
        nameof(Opportunity.AmountOriginal),
        nameof(Opportunity.WinProbability),
        nameof(Opportunity.CloseInPeriodProbability),
        nameof(Opportunity.GmPercent),
        nameof(Opportunity.Ramp),
        nameof(Opportunity.Otp),
        nameof(Opportunity.Notes),
    };

    private static readonly Dictionary<string, string> Rotulos = new(StringComparer.Ordinal)
    {
        [nameof(Opportunity.ExpectedDate)] = "Data prevista",
        [nameof(Opportunity.AmountOriginal)] = "Net Value",
        [nameof(Opportunity.WinProbability)] = "% de Ganho",
        [nameof(Opportunity.CloseInPeriodProbability)] = "% de Sair no Mês",
        [nameof(Opportunity.GmPercent)] = "PM %",
        [nameof(Opportunity.Ramp)] = "RAMP",
        [nameof(Opportunity.Otp)] = "OTP",
        [nameof(Opportunity.Notes)] = "Observação",
    };

    private static readonly HashSet<string> Numericos = new(StringComparer.Ordinal)
    {
        nameof(Opportunity.AmountOriginal), nameof(Opportunity.WinProbability),
        nameof(Opportunity.CloseInPeriodProbability), nameof(Opportunity.GmPercent),
    };

    private static readonly Dictionary<string, PropertyInfo> Props =
        Protegidos.ToDictionary(n => n, n => typeof(Opportunity).GetProperty(n)!, StringComparer.Ordinal);

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Rotulo(string campo) => Rotulos.TryGetValue(campo, out var r) ? r : campo;

    public static Dictionary<string, string> Ler(string? snapshot)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(snapshot)) return d;
        foreach (var item in snapshot.Split(SepCampo))
        {
            var i = item.IndexOf(SepValor);
            if (i > 0) d[item[..i]] = item[(i + 1)..];
        }
        return d;
    }

    // "0" conta como ausência: os numéricos nascem em "0" e o importador devolve
    // "0" quando a coluna vem vazia — não é uma informação da planilha.
    public static bool SemInfo(string? v) => string.IsNullOrWhiteSpace(v) || v.Trim() == "0";

    /// <summary>Valor que o CRM trouxe por último para o campo; null quando a
    /// oportunidade não tem memória do CRM (criada à mão, ou gravada antes de a
    /// memória existir) — nesse caso não há com o que comparar.</summary>
    public static string? ValorCrm(Opportunity o, string campo) =>
        Ler(o.CrmSnapshot).TryGetValue(campo, out var v) ? v : null;

    public static string Valor(Opportunity o, string campo) =>
        Props.TryGetValue(campo, out var p) ? (string?)p.GetValue(o) ?? "" : "";

    /// <summary>O valor do sistema substitui um valor que o CRM trouxe? Compara
    /// pelo tipo do campo: número com número, data por mês (o formulário só edita
    /// mês e ano), texto sem espaços sobrando nem diferença de caixa.</summary>
    public static bool Diferente(string campo, string? crm, string? sistema)
    {
        if (SemInfo(crm)) return false;                 // nada do CRM a ser substituído
        if (campo == nameof(Opportunity.ExpectedDate))
        {
            var a = Data(crm); var b = Data(sistema);
            if (a is null) return false;
            return b is null || a.Value.Year != b.Value.Year || a.Value.Month != b.Value.Month;
        }
        if (Numericos.Contains(campo))
            return Math.Abs(Num(crm) - Num(sistema)) > 0.000001;
        return !string.Equals((crm ?? "").Trim(), (sistema ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Os dois valores são outros, pelo tipo do campo — SEM a isenção
    /// do "CRM vazio". É a pergunta "mudou?" entre antes e depois de uma edição,
    /// usada para carimbar data e valor ao salvar.</summary>
    public static bool Mudou(string campo, string? antes, string? depois)
    {
        if (SemInfo(antes) && SemInfo(depois)) return false;
        if (SemInfo(antes) != SemInfo(depois)) return true;
        return Diferente(campo, antes, depois);
    }

    // Carimbo de edição à mão: data e valor têm o seu (gravado ao salvar no
    // formulário). É o que faz a alteração aparecer mesmo quando o CRM não
    // tinha nada naquele campo — a pessoa mudou o que estava no sistema, e é
    // isso que ela lembra (ou esquece) ter feito.
    private static string? Carimbo(Opportunity o, string campo) => campo switch
    {
        nameof(Opportunity.ExpectedDate) => o.DateChangedAt,
        nameof(Opportunity.AmountOriginal) => o.ValueChangedAt,
        _ => null,
    };

    /// <summary>O campo foi alterado no sistema? Sim quando (a) o valor gravado
    /// substitui um valor que o CRM trouxe, ou (b) tem carimbo de edição à mão e
    /// o CRM não tem esse valor (não trouxe, ou trouxe vazio).</summary>
    private static bool Avalia(Opportunity o, string campo, Dictionary<string, string> crm, out string? valorCrm)
    {
        valorCrm = crm.TryGetValue(campo, out var v) ? v : null;
        var aqui = Valor(o, campo);
        if (valorCrm is not null && Diferente(campo, valorCrm, aqui)) return true;
        if (string.IsNullOrWhiteSpace(Carimbo(o, campo)) || SemInfo(aqui)) return false;
        return valorCrm is null || Mudou(campo, valorCrm, aqui);
    }

    public static bool Diferente(Opportunity o, string campo) => Avalia(o, campo, Ler(o.CrmSnapshot), out _);

    public static bool Alterada(Opportunity o)
    {
        var crm = Ler(o.CrmSnapshot);
        foreach (var campo in Protegidos)
            if (Avalia(o, campo, crm, out _)) return true;
        return false;
    }

    public static List<Campo> Alterados(Opportunity o)
    {
        var lista = new List<Campo>();
        var crm = Ler(o.CrmSnapshot);
        foreach (var campo in Protegidos)
            if (Avalia(o, campo, crm, out var v)) lista.Add(new Campo(campo, Rotulo(campo), v ?? "", Valor(o, campo)));
        return lista;
    }

    /// <summary>Valor formatado para a pessoa ler: data em dd/MM/aaaa, número com
    /// separador brasileiro, % com o sinal, vazio como "—".</summary>
    public static string Mostrar(string campo, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return "—";
        if (campo == nameof(Opportunity.ExpectedDate)) return Fmt.Date(valor);
        if (campo == nameof(Opportunity.AmountOriginal)) return Num(valor).ToString("#,##0.##", Pt);
        if (Numericos.Contains(campo)) return Num(valor).ToString("0.##", Pt) + "%";
        return valor.Trim();
    }

    /// <summary>Data (ISO) da alteração do campo: data e valor têm carimbo próprio;
    /// os demais usam a última gravação da oportunidade. "" se não há registro.</summary>
    public static string Quando(Opportunity o, string campo) => campo switch
    {
        nameof(Opportunity.ExpectedDate) when !string.IsNullOrWhiteSpace(o.DateChangedAt) => o.DateChangedAt,
        nameof(Opportunity.AmountOriginal) when !string.IsNullOrWhiteSpace(o.ValueChangedAt) => o.ValueChangedAt,
        _ => o.UpdatedAt ?? "",
    };

    public static string Quem(Opportunity o) =>
        string.IsNullOrWhiteSpace(o.UpdatedBy) || o.UpdatedBy == "—" ? "" : o.UpdatedBy.Trim();

    /// <summary>Quem alterou e quando, do jeito que cabe numa frase; "" se não há registro.</summary>
    public static string QuemQuando(Opportunity o, string campo)
    {
        var quando = Quando(o, campo);
        var quem = Quem(o);
        var s = "";
        if (quem != "") s += " por " + quem;
        if (!string.IsNullOrWhiteSpace(quando)) s += " em " + Fmt.Date(quando);
        return s;
    }

    /// <summary>Texto da dica sobre o campo; null quando o campo não foi alterado
    /// (assim o atributo nem é gerado).</summary>
    public static string? Descricao(Opportunity o, string campo)
    {
        if (!Avalia(o, campo, Ler(o.CrmSnapshot), out var crm)) return null;
        return $"Alterado no sistema{QuemQuando(o, campo)} · No CRM: {Mostrar(campo, crm)} · Aqui: {Mostrar(campo, Valor(o, campo))}";
    }

    private static double Num(string? s)
    {
        double.TryParse((s ?? "").Trim(), NumberStyles.Any, Inv, out var v);
        return v;
    }

    private static DateTime? Data(string? s) =>
        DateTime.TryParse((s ?? "").Trim(), Inv, DateTimeStyles.None, out var d) ? d : null;
}
