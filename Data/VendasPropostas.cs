using System.Globalization;
using System.Data;
using ClosedXML.Excel;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Guia Vendas × Propostas: as vendas mês a mês ao longo dos anos, os budgets
// (HSA e Grupo) e as propostas enviadas em cada mês. As propostas vêm das
// planilhas da guia Conversão (AFM e NB, pela data de envio) ou, para o
// histórico de antes delas, do que for informado à mão.
// ---------------------------------------------------------------------------

/// <summary>As vendas de um mês e, quando informadas à mão, as propostas enviadas nele.</summary>
public sealed class MesDeVendas
{
    public int Ano { get; set; }
    public int Mes { get; set; }
    /// <summary>R$ vendidos no mês; null = ainda sem número (o mês não fechou).</summary>
    public decimal? Vendas { get; set; }
    /// <summary>Propostas enviadas no mês, informadas à mão (o histórico de antes do sistema); null = vale a contagem do sistema.</summary>
    public int? Propostas { get; set; }
    /// <summary>Valor total das propostas enviadas no mês (R$), informado à mão; null = o do sistema.</summary>
    public decimal? ValorProposto { get; set; }

    public string Id => $"{Ano:D4}-{Mes:D2}";
}

/// <summary>Os budgets de um ano (R$): o da HSA e o do grupo.</summary>
public sealed class BudgetDoAno
{
    public int Ano { get; set; }
    public decimal? Hsa { get; set; }
    public decimal? Grupo { get; set; }
}

/// <summary>
/// As vendas mês a mês, os budgets e as propostas informadas (entidades
/// "vendas_mensais" e "budgets_anuais"). Na primeira vez, já vem com o
/// histórico da planilha da equipe (vendas de jan/2022 a out/2026 e budgets).
/// </summary>
public sealed class VendasMensaisRepository
{
    private const string EntidadeMeses = "vendas_mensais";
    private const string EntidadeBudgets = "budgets_anuais";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ParquetStore _s;
    public VendasMensaisRepository(ParquetStore s) => _s = s;

    public List<MesDeVendas> Meses()
    {
        if (_s.IsEmpty(EntidadeMeses)) GravarMeses(HistoricoDaPlanilha.Meses());
        return _s.ReadLatest(EntidadeMeses, "id, vendas, propostas, valor_proposto", r =>
            {
                var id = Txt(r, 0);
                return new MesDeVendas
                {
                    Ano = int.TryParse(id.Split('-')[0], out var a) ? a : 0,
                    Mes = id.Length >= 7 && int.TryParse(id[5..7], out var m) ? m : 0,
                    Vendas = Dec(Txt(r, 1)),
                    Propostas = int.TryParse(Txt(r, 2), NumberStyles.Integer, Inv, out var p) ? p : null,
                    ValorProposto = Dec(Txt(r, 3)),
                };
            })
            .Where(x => x.Ano > 0 && x.Mes is >= 1 and <= 12)
            .OrderBy(x => x.Ano).ThenBy(x => x.Mes).ToList();
    }

    public List<BudgetDoAno> Budgets()
    {
        if (_s.IsEmpty(EntidadeBudgets)) GravarBudgets(HistoricoDaPlanilha.Budgets());
        return _s.ReadLatest(EntidadeBudgets, "id, hsa, grupo", r => new BudgetDoAno
            {
                Ano = int.TryParse(Txt(r, 0), out var a) ? a : 0,
                Hsa = Dec(Txt(r, 1)),
                Grupo = Dec(Txt(r, 2)),
            })
            .Where(b => b.Ano > 0).OrderBy(b => b.Ano).ToList();
    }

    public void GravarMeses(IEnumerable<MesDeVendas> meses) =>
        _s.WriteBatch(EntidadeMeses, meses.Select(m => (IReadOnlyList<KeyValuePair<string, object?>>)new KeyValuePair<string, object?>[]
        {
            new("id", m.Id), new("vendas", S(m.Vendas)),
            new("propostas", m.Propostas?.ToString(Inv) ?? ""), new("valor_proposto", S(m.ValorProposto)),
        }).ToList());

    public void GravarBudgets(IEnumerable<BudgetDoAno> budgets) =>
        _s.WriteBatch(EntidadeBudgets, budgets.Select(b => (IReadOnlyList<KeyValuePair<string, object?>>)new KeyValuePair<string, object?>[]
        {
            new("id", b.Ano.ToString(Inv)), new("hsa", S(b.Hsa)), new("grupo", S(b.Grupo)),
        }).ToList());

    private static string S(decimal? v) => v?.ToString(Inv) ?? "";
    private static decimal? Dec(string s) => decimal.TryParse(s, NumberStyles.Number, Inv, out var v) ? v : null;
    private static string Txt(IDataReader r, int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), Inv) ?? "";

    /// <summary>Minúsculas, sem acento, espaços colapsados (para casar rótulos como "Mês", "BUDGET HSA").</summary>
    public static string Simples(string? s) => OpportunityImporter.Normalizar(s ?? "");

    /// <summary>Número digitado ou lido da planilha: "33.253.000", "33253000", "33,5", "R$ 1.234,56", "-6.104.000".</summary>
    public static decimal? Numero(string? texto)
    {
        var t = (texto ?? "").Replace("R$", "").Replace("US$", "").Replace("\u00a0", "").Replace(" ", "").Replace("−", "-").Trim();
        if (t.Length == 0) return null;
        var neg = t.StartsWith('(') && t.EndsWith(')');
        if (neg) t = t[1..^1];
        int v = t.LastIndexOf(','), p = t.LastIndexOf('.');
        if (v >= 0 && p >= 0) t = v > p ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (v >= 0) t = t.Count(c => c == ',') > 1 ? t.Replace(",", "") : t.Replace(',', '.');
        else if (p >= 0 && (t.Count(c => c == '.') > 1 || t.Length - p - 1 == 3)) t = t.Replace(".", "");
        if (!decimal.TryParse(t, NumberStyles.Float, Inv, out var r)) return null;
        return neg ? -r : r;
    }

    // ================= planilha (no layout da equipe: Mês × anos) =================

    public static readonly string[] NomesDosMeses =
        { "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho", "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro" };

    public static readonly string[] MesesCurtos = { "Jan", "Fev", "Mar", "Abr", "Mai", "Jun", "Jul", "Ago", "Set", "Out", "Nov", "Dez" };

    /// <summary>O mês pelo nome ("Janeiro", "jan", "JAN.", "1").</summary>
    public static int? MesDe(string? texto)
    {
        var t = Simples(texto).Trim('.', ' ');
        if (int.TryParse(t, out var n) && n is >= 1 and <= 12) return n;
        if (t.Length < 3) return null;
        for (var i = 0; i < 12; i++)
            if (Simples(NomesDosMeses[i]).StartsWith(t[..3])) return i + 1;
        return null;
    }

    public enum TipoDeImportacao { Vendas, Propostas, ValorProposto }

    /// <summary>
    /// Lê a planilha no layout da equipe: a coluna "Mês" e uma coluna por ano;
    /// nas vendas, as linhas "BUDGET HSA" e "BUDGET GRUPO" viram os budgets
    /// ("TOTAL" é ignorado: o sistema soma). Grava por cima do mesmo mês.
    /// </summary>
    public (int Meses, int Budgets, List<string> Avisos) Importar(byte[] arquivo, string nome, TipoDeImportacao tipo)
    {
        var avisos = new List<string>();
        using var ms = new MemoryStream(arquivo);
        var todas = OpportunityImporter.LerGrade(nome, ms)
            .Select(l => l.Select(c => c.Num is double n ? n.ToString(Inv) : c.Text).ToArray()).ToList();
        var iCab = todas.FindIndex(l => l.Length > 1 && Simples(l[0]).StartsWith("mes"));
        if (iCab < 0) iCab = todas.FindIndex(l => l.Skip(1).Count(c => int.TryParse((c ?? "").Trim().Split('.')[0], out var a) && a is > 2000 and < 2100) >= 1);
        if (iCab < 0) return (0, 0, new List<string> { "Não achei o cabeçalho: a 1ª coluna \"Mês\" e uma coluna por ano (2022, 2023…)." });

        var anos = new Dictionary<int, int>();   // coluna → ano
        for (var c = 1; c < todas[iCab].Length; c++)
            if (int.TryParse((todas[iCab][c] ?? "").Trim().Split('.')[0], out var a) && a is > 2000 and < 2100) anos[c] = a;
        if (anos.Count == 0) return (0, 0, new List<string> { "O cabeçalho não tem os anos (2022, 2023…)." });

        var meses = Meses().ToDictionary(m => m.Id);
        var budgets = Budgets().ToDictionary(b => b.Ano);
        var mudados = new Dictionary<string, MesDeVendas>();
        var budgetsMudados = new Dictionary<int, BudgetDoAno>();
        foreach (var l in todas.Skip(iCab + 1))
        {
            if (l.Length == 0) continue;
            var rotulo = Simples(l[0]);
            var mes = MesDe(l[0]);
            var budgetHsa = rotulo.Contains("budget") && rotulo.Contains("hsa");
            var budgetGrupo = rotulo.Contains("budget") && rotulo.Contains("grupo");
            if (mes is null && !(tipo == TipoDeImportacao.Vendas && (budgetHsa || budgetGrupo))) continue;
            foreach (var (c, ano) in anos)
            {
                var texto = c < l.Length ? (l[c] ?? "").Trim() : "";
                if (texto.Length == 0 || texto is "-" or "—") continue;
                var v = Numero(texto);
                if (v is null) { avisos.Add($"{l[0]} {ano}: \"{texto}\" não é número."); continue; }
                if (mes is int m)
                {
                    var id = $"{ano:D4}-{m:D2}";
                    var reg = mudados.GetValueOrDefault(id) ?? Copia(meses.GetValueOrDefault(id)) ?? new MesDeVendas { Ano = ano, Mes = m };
                    switch (tipo)
                    {
                        case TipoDeImportacao.Vendas: reg.Vendas = v; break;
                        case TipoDeImportacao.Propostas: reg.Propostas = (int)Math.Round(v.Value); break;
                        default: reg.ValorProposto = v; break;
                    }
                    mudados[id] = reg;
                }
                else
                {
                    var b = budgetsMudados.GetValueOrDefault(ano) ?? budgets.GetValueOrDefault(ano) ?? new BudgetDoAno { Ano = ano };
                    if (budgetHsa) b.Hsa = v; else b.Grupo = v;
                    budgetsMudados[ano] = b;
                }
            }
        }
        if (mudados.Count > 0) GravarMeses(mudados.Values);
        if (budgetsMudados.Count > 0) GravarBudgets(budgetsMudados.Values);
        return (mudados.Count, budgetsMudados.Count, avisos);
    }

    private static MesDeVendas? Copia(MesDeVendas? m) => m is null ? null
        : new MesDeVendas { Ano = m.Ano, Mes = m.Mes, Vendas = m.Vendas, Propostas = m.Propostas, ValorProposto = m.ValorProposto };

    /// <summary>A planilha com as três abas (vendas com totais e budgets, propostas e valor proposto), no layout da equipe.</summary>
    public static byte[] Planilha(IReadOnlyList<MesDeVendas> meses, IReadOnlyList<BudgetDoAno> budgets, IReadOnlyList<ResumoDoMes>? conversao = null)
    {
        var anos = meses.Select(m => m.Ano).Concat(budgets.Select(b => b.Ano)).Distinct().OrderBy(a => a).ToList();
        using var wb = new XLWorkbook();

        void Aba(string nome, Func<int, int, decimal?> valor, bool reais, bool comBudgets)
        {
            var ws = wb.Worksheets.Add(nome);
            ws.Cell(1, 1).SetValue("Mês");
            for (var i = 0; i < anos.Count; i++) ws.Cell(1, i + 2).SetValue(anos[i]);
            var cab = ws.Range(1, 1, 1, anos.Count + 1);
            cab.Style.Font.Bold = true; cab.Style.Fill.BackgroundColor = XLColor.FromHtml("#9DC3E6");
            for (var m = 1; m <= 12; m++)
            {
                ws.Cell(m + 1, 1).SetValue(NomesDosMeses[m - 1]);
                ws.Cell(m + 1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#BDD7EE");
                for (var i = 0; i < anos.Count; i++)
                    if (valor(anos[i], m) is decimal v) ws.Cell(m + 1, i + 2).SetValue(v);
            }
            ws.Cell(14, 1).SetValue("TOTAL").Style.Font.Bold = true;
            for (var i = 0; i < anos.Count; i++)
            {
                var col = XLHelper.GetColumnLetterFromNumber(i + 2);
                ws.Cell(14, i + 2).FormulaA1 = $"SUM({col}2:{col}13)";
                ws.Cell(14, i + 2).Style.Font.Bold = true;
            }
            ws.Range(14, 1, 14, anos.Count + 1).Style.Border.TopBorder = XLBorderStyleValues.Medium;
            if (comBudgets)
            {
                ws.Cell(15, 1).SetValue("BUDGET HSA");
                ws.Cell(16, 1).SetValue("BUDGET GRUPO");
                for (var i = 0; i < anos.Count; i++)
                {
                    var b = budgets.FirstOrDefault(x => x.Ano == anos[i]);
                    if (b?.Hsa is decimal h) ws.Cell(15, i + 2).SetValue(h);
                    if (b?.Grupo is decimal g) ws.Cell(16, i + 2).SetValue(g);
                }
            }
            var fmt = reais ? "\"R$\" #,##0.00;-\"R$\" #,##0.00" : "#,##0";
            ws.Range(2, 2, 16, anos.Count + 1).Style.NumberFormat.Format = fmt;
            ws.Column(1).Width = 16;
            for (var i = 0; i < anos.Count; i++) ws.Column(i + 2).Width = 20;
            ws.SheetView.FreezeRows(1);
        }

        MesDeVendas? M(int a, int m) => meses.FirstOrDefault(x => x.Ano == a && x.Mes == m);
        Aba("Vendas", (a, m) => M(a, m)?.Vendas, true, true);
        if (conversao is not null)
        {
            ResumoDoMes? C(int a, int m) => conversao.FirstOrDefault(x => x.Ano == a && x.Mes == m);
            Aba("Propostas enviadas", (a, m) => C(a, m)?.Propostas, false, false);
            Aba("Valor proposto", (a, m) => C(a, m)?.ValorProposto, true, false);
        }
        else
        {
            Aba("Propostas enviadas", (a, m) => M(a, m)?.Propostas, false, false);
            Aba("Valor proposto", (a, m) => M(a, m)?.ValorProposto, true, false);
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

/// <summary>O histórico da planilha da equipe (vendas mês a mês 2022–2026 e budgets), o ponto de partida da guia.</summary>
public static class HistoricoDaPlanilha
{
    private static readonly Dictionary<int, decimal?[]> Vendas = new()
    {
        [2022] = new decimal?[] { 12_060_000, 11_844_000, 21_214_000, 27_490_000, 30_547_000, 23_592_000, 43_086_000, 16_015_000, 40_090_000, 22_702_000, 45_818_000, 78_163_000 },
        [2023] = new decimal?[] { 9_358_000, 34_258_000, 34_166_000, 25_850_000, 29_194_000, 81_787_000, 25_472_000, 22_116_000, 35_092_000, 17_303_000, 22_529_000, 63_466_000 },
        [2024] = new decimal?[] { 12_598_000, 9_427_000, 46_516_000, 20_423_000, 47_078_000, 135_599_000, 43_018_000, 51_480_000, 35_037_000, 41_914_000, 42_479_000, 38_305_000 },
        [2025] = new decimal?[] { 19_887_000, 26_453_000, 37_379_000, 19_850_000, 24_482_000, 22_667_000, 60_882_000, 42_101_000, 42_144_000, 63_042_000, 74_965_000, 18_627_000 },
        [2026] = new decimal?[] { 31_367_000, 30_720_000, 21_912_000, 43_201_000, 41_680_000, 44_027_000, -6_104_000, 44_271_000, 52_976_000, 33_253_000, null, null },
    };

    public static List<MesDeVendas> Meses() =>
        Vendas.SelectMany(kv => kv.Value.Select((v, i) => (kv.Key, Mes: i + 1, v)))
            .Where(x => x.v is not null)
            .Select(x => new MesDeVendas { Ano = x.Key, Mes = x.Mes, Vendas = x.v })
            .ToList();

    public static List<BudgetDoAno> Budgets() => new()
    {
        new() { Ano = 2022, Hsa = null, Grupo = 310_000_000 },
        new() { Ano = 2023, Hsa = 500_000_000, Grupo = 380_000_000 },
        new() { Ano = 2024, Hsa = 650_000_000, Grupo = 500_000_000 },
        new() { Ano = 2025, Hsa = 660_000_000, Grupo = 593_201_000 },
        new() { Ano = 2026, Hsa = 643_501_000, Grupo = 550_000_000 },
    };
}

/// <summary>Um mês no painel: as vendas e as propostas enviadas (do sistema ou informadas).</summary>
public sealed record ResumoDoMes(int Ano, int Mes, decimal? Vendas, int? Propostas, decimal? ValorProposto, string FonteDasPropostas)
{
    public bool TemPropostas => Propostas is not null;
}

/// <summary>Um ano no painel.</summary>
/// <param name="Propostas">todas as propostas enviadas no ano (com ou sem venda registrada no mês)</param>
/// <param name="PropostasComVendas">as propostas dos meses que também têm a venda: a base das relações</param>
/// <param name="VendasNosMesesComPropostas">as vendas desses mesmos meses</param>
/// <param name="ValorPropostoComVendas">o valor proposto desses mesmos meses (null = não informado)</param>
/// <param name="MesesComAmbos">quantos meses têm venda e propostas</param>
public sealed record ResumoDoAno(int Ano, decimal Vendas, int MesesComVendas, int UltimoMes, decimal? BudgetHsa, decimal? BudgetGrupo,
    int? Propostas, decimal? ValorProposto, int PropostasComVendas, decimal VendasNosMesesComPropostas, decimal? ValorPropostoComVendas,
    int MesesComPropostas, int MesesComAmbos)
{
    public decimal? PctHsa => BudgetHsa is > 0 ? Vendas / BudgetHsa : null;
    public decimal? PctGrupo => BudgetGrupo is > 0 ? Vendas / BudgetGrupo : null;
    /// <summary>R$ vendido por proposta enviada (só nos meses que têm as duas coisas).</summary>
    public decimal? VendaPorProposta => PropostasComVendas > 0 && VendasNosMesesComPropostas > 0 ? VendasNosMesesComPropostas / PropostasComVendas : null;
    /// <summary>Propostas para cada R$ 1 milhão vendido.</summary>
    public decimal? PropostasPorMilhao => VendasNosMesesComPropostas > 0 && PropostasComVendas > 0 ? PropostasComVendas / (VendasNosMesesComPropostas / 1_000_000m) : null;
    /// <summary>Quantas propostas o budget HSA pede, ao ritmo de venda por proposta deste ano.</summary>
    public int? PropostasParaOBudget => VendaPorProposta is > 0 && BudgetHsa is > 0 ? (int)Math.Ceiling(BudgetHsa.Value / VendaPorProposta.Value) : null;
    /// <summary>O quanto do valor proposto virou venda (nos mesmos meses).</summary>
    public decimal? Conversao => ValorPropostoComVendas is > 0 ? VendasNosMesesComPropostas / ValorPropostoComVendas : null;
    /// <summary>Que parte dos meses com venda tem as propostas registradas.</summary>
    public decimal Cobertura => MesesComVendas > 0 ? (decimal)MesesComAmbos / MesesComVendas : 0m;
    /// <summary>Dá para responder "quantas propostas" com este ano: pelo menos 3 meses e metade dos meses com as duas coisas.</summary>
    public bool Representativo => MesesComAmbos >= 3 && Cobertura >= 0.5m && VendaPorProposta is > 0;
}

/// <summary>
/// Vendas × propostas: quanto vendemos mês a mês e quantas propostas
/// enviamos para chegar nesse valor. As propostas de cada mês vêm do sistema
/// (propostas marcadas como enviadas e o histórico importado nas Referências
/// de preço, cada número de proposta contado uma vez, no mês do primeiro
/// envio) ou, para o histórico de antes do sistema, do que foi informado.
/// </summary>
public static class ConversaoDeVendas
{
    /// <summary>Uma proposta enviada: a data de envio, o valor em R$ e quem respondeu por ela.</summary>
    public sealed record PropostaEnviada(string Codigo, DateTime Data, decimal ValorReais, string Fonte, string Vendedor,
        ClasseProposta Classe, bool SemTaxa);

    /// <summary>
    /// As propostas das planilhas da guia Conversão (AFM e NB) com data de envio.
    /// Com número de proposta, cada número conta uma vez (a primeira data de envio
    /// e o maior valor); sem ele, cada linha é uma proposta. O AFM vem em US$ e
    /// passa a R$ pela taxa do ano (a mesma da guia Conversão); o NB já vem em R$.
    /// </summary>
    public static List<PropostaEnviada> PropostasEnviadas(IEnumerable<PropostaConv> propostas, IReadOnlyDictionary<int, double> taxas)
    {
        var lista = new List<PropostaEnviada>();
        foreach (var p in propostas)
        {
            if (p.Data is not DateTime d) continue;
            double? brl = p.ValorBrl ?? (p.ValorUsd is double u && taxas.TryGetValue(d.Year, out var t) ? u * t : null);
            var semTaxa = p.ValorBrl is null && p.ValorUsd is not null && brl is null;
            lista.Add(new PropostaEnviada(p.Chave.Trim().Length > 0 ? p.Origem + ":" + p.Chave.Trim().ToUpperInvariant() : $"{p.Origem}#{p.Linha}",
                d.Date, (decimal)Math.Round(brl ?? 0, 2), p.Origem, p.Vendedor.Trim().Length > 0 ? p.Vendedor.Trim() : ConversaoService.NaoInformado,
                p.Classe, semTaxa));
        }
        return lista.GroupBy(p => p.Codigo)
            .Select(g => g.Count() == 1 ? g.First() : g.OrderBy(p => p.Data).First() with { ValorReais = g.Max(p => p.ValorReais) })
            .OrderBy(p => p.Data).ToList();
    }

    /// <summary>Os meses do painel: as vendas e as propostas (informadas à mão valem mais que as do sistema).</summary>
    public static List<ResumoDoMes> Meses(IReadOnlyList<MesDeVendas> vendas, IReadOnlyList<PropostaEnviada> enviadas)
    {
        var doSistema = enviadas.GroupBy(p => (p.Data.Year, p.Data.Month))
            .ToDictionary(g => g.Key, g => (Qtd: g.Count(), Valor: g.Sum(p => p.ValorReais)));
        var chaves = vendas.Select(v => (v.Ano, v.Mes)).Concat(doSistema.Keys).Distinct().OrderBy(k => k.Item1).ThenBy(k => k.Item2);
        return chaves.Select(k =>
        {
            var v = vendas.FirstOrDefault(x => x.Ano == k.Item1 && x.Mes == k.Item2);
            if (v?.Propostas is int informadas)
                return new ResumoDoMes(k.Item1, k.Item2, v.Vendas, informadas, v.ValorProposto, "informado");
            if (doSistema.TryGetValue(k, out var s))
                return new ResumoDoMes(k.Item1, k.Item2, v?.Vendas, s.Qtd, s.Valor > 0m ? s.Valor : null, "sistema");
            return new ResumoDoMes(k.Item1, k.Item2, v?.Vendas, null, null, "");
        }).ToList();
    }

    public static List<ResumoDoAno> Anos(IReadOnlyList<ResumoDoMes> meses, IReadOnlyList<BudgetDoAno> budgets)
    {
        var anos = meses.Select(m => m.Ano).Concat(budgets.Select(b => b.Ano)).Distinct().OrderBy(a => a);
        return anos.Select(a =>
        {
            var doAno = meses.Where(m => m.Ano == a).ToList();
            var comVendas = doAno.Where(m => m.Vendas is not null).ToList();
            var comPropostas = doAno.Where(m => m.TemPropostas).ToList();
            var ambos = comPropostas.Where(m => m.Vendas is not null).ToList();
            var b = budgets.FirstOrDefault(x => x.Ano == a);
            var valor = comPropostas.Any(m => m.ValorProposto is not null) ? comPropostas.Sum(m => m.ValorProposto ?? 0m) : (decimal?)null;
            var valorAmbos = ambos.Any(m => m.ValorProposto is not null) ? ambos.Sum(m => m.ValorProposto ?? 0m) : (decimal?)null;
            return new ResumoDoAno(a, comVendas.Sum(m => m.Vendas!.Value), comVendas.Count, comVendas.Count > 0 ? comVendas.Max(m => m.Mes) : 0,
                b?.Hsa, b?.Grupo,
                comPropostas.Count > 0 ? comPropostas.Sum(m => m.Propostas!.Value) : null, valor,
                ambos.Sum(m => m.Propostas!.Value), ambos.Sum(m => m.Vendas!.Value), valorAmbos,
                comPropostas.Count, ambos.Count);
        }).ToList();
    }

    /// <summary>Vendas do ano até o mês dado (o mesmo período, para comparar com o ano anterior).</summary>
    public static decimal AteOMes(IReadOnlyList<ResumoDoMes> meses, int ano, int mes) =>
        meses.Where(m => m.Ano == ano && m.Mes <= mes).Sum(m => m.Vendas ?? 0m);
}
