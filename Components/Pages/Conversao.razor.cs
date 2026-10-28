using System.Text;
using HowdenSalesForecast.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace HowdenSalesForecast.Components.Pages;

// ===========================================================================
// Conversão · lógica analítica.
//
// UMA fonte de números: Recalcular() monta _itens (planilha + câmbio do ano da
// proposta) e _f (itens depois dos filtros globais). Overview, Deep Dive, Base de
// Propostas e o relatório leem _f / _fx e Somar() — nenhuma tela tem conta própria.
//
//  _f  = filtros globais (ano, mês, vendedor, BU, BU Intercompany)
//  _fx = _f + segmento clicado na barra de composição (cross-filter). Com um
//        segmento ativo, taxa de ganho não faz sentido (só ganhas, só perdidas…)
//        e aparece como "—".
// ===========================================================================
public partial class Conversao
{
    // ---- cultura, cores e nomes ------------------------------------------------
    private static readonly System.Globalization.CultureInfo _pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    // Um conceito, uma cor, em todas as visualizações.
    private const string CorG = "#1f9254", CorP = "#d98324", CorA = "#007A9E", CorN = "#93a1b5";
    private static readonly ClasseProposta[] Classes =
        { ClasseProposta.Ganha, ClasseProposta.Perdida, ClasseProposta.Aberta, ClasseProposta.NaoClassificada };

    private static string Cor(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => CorG, ClasseProposta.Perdida => CorP, ClasseProposta.Aberta => CorA, _ => CorN,
    };
    private static string NomeEn(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => "Ganhas", ClasseProposta.Perdida => "Perdidas", ClasseProposta.Aberta => "Abertas", _ => "Não classificadas",
    };
    private static string NomeClasse(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => "Ganha", ClasseProposta.Perdida => "Perdida", ClasseProposta.Aberta => "Aberta", _ => "Não classificada",
    };
    private static string DefClasse(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => "Ganhas: status Closed - Won ou OTP.",
        ClasseProposta.Perdida => "Perdidas: Cancelled, Closed - Customer Lost, Closed - Lost, Closed - No Bid ou Closed - Unknown.",
        ClasseProposta.Aberta => "Open: status Open — ainda sem resultado.",
        _ => "Unclassified: status fora da lista combinada, aguardando validação. Fica fora de ganhas, perdidas e abertas.",
    };
    private static string ClasseCss(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => "g", ClasseProposta.Perdida => "p", ClasseProposta.Aberta => "a", _ => "n",
    };

    // ---- sessão ----------------------------------------------------------------
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }
    private bool _isAdmin, _carregando;
    private string _login = "", _user = "—";
    private bool _temDados => Conv.Atual.Propostas.Count > 0;

    // ---- filtros globais --------------------------------------------------------
    private static HashSet<string> Novo() => new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _anos = Novo(), _meses = Novo(), _vend = Novo(), _bus = Novo(), _buIcs = Novo();
    private ClasseProposta? _seg;                              // cross-filter da barra de composição
    private bool _taxaOk => _seg is null;                      // taxa de ganho só vale sem segmento
    private int Ativos => new[] { _anos, _meses, _vend, _bus, _buIcs }.Count(s => s.Count > 0) + (_seg is null ? 0 : 1);
    private IEnumerable<(string, HashSet<string>)> ConjuntosAtivos() =>
        new[] { ("Ano", _anos), ("Mês", _meses), ("Vendedor", _vend), ("BU", _bus), ("BU Intercompany", _buIcs) }.Where(x => x.Item2.Count > 0);
    private void Limpar() { _anos.Clear(); _meses.Clear(); _vend.Clear(); _bus.Clear(); _buIcs.Clear(); _seg = null; _pag = 0; _offset = 0; }
    private void AlternarSeg(ClasseProposta c) => _seg = _seg == c ? null : c;

    private static void Alternar(HashSet<string> set, string v) { if (!set.Remove(v)) set.Add(v); }

    // ---- modelo calculado --------------------------------------------------------
    private sealed record Item(PropostaConv P, int? Ano, int? Mes, double? Taxa, double? Brl);

    private sealed class Resumo
    {
        public int Total, G, P, A, NC, SemTaxa, SemValor;
        public double VG, VP, VA, VNC, UsdSemTaxa;
        /// <summary>Por classe: quantas têm valor em US$ e quantas foram convertidas (têm taxa do ano).</summary>
        public readonly int[] ComUsd = new int[4];
        public readonly int[] Conv = new int[4];
        public int Decididas => G + P;
        public double VTotal => VG + VP + VA + VNC;
        public double? TaxaQtd => Decididas > 0 ? (double)G / Decididas * 100 : null;
        public double? TaxaValor => (VG + VP) > 0 ? VG / (VG + VP) * 100 : null;
        public int Qtd(ClasseProposta c) => c switch { ClasseProposta.Ganha => G, ClasseProposta.Perdida => P, ClasseProposta.Aberta => A, _ => NC };
        public double Val(ClasseProposta c) => c switch { ClasseProposta.Ganha => VG, ClasseProposta.Perdida => VP, ClasseProposta.Aberta => VA, _ => VNC };
        public int TotComUsd => ComUsd.Sum();
        public int TotConv => Conv.Sum();
    }

    private List<Item> _itens = new();
    private List<Item> _f = new(), _fx = new();
    private Resumo _r = new(), _rx = new();
    private Resumo? _prev;
    private (int, int) _chave = (-1, -1);
    private int _offset, _offsetMax;                 // navegação histórica da tendência (meses para trás)

    private void Recalcular()
    {
        var chave = (Conv.Version, Cambio.Version);
        if (chave != _chave) { Reconstruir(); _chave = chave; }
        _f = Aplicar().ToList();
        _fx = _seg is { } s ? _f.Where(i => i.P.Classe == s).ToList() : _f;
        _r = Somar(_f);
        _rx = ReferenceEquals(_fx, _f) ? _r : Somar(_fx);
        _prev = null;
        // Período comparável: só com UM ano escolhido — mesmos filtros e meses, ano anterior.
        if (_anos.Count == 1 && int.TryParse(_anos.First(), out var a))
        {
            var p = Somar(Aplicar("ano").Where(i => i.Ano == a - 1));
            if (p.Total > 0) _prev = p;
        }
        var meses = _f.Where(i => i.Ano is not null).Select(i => i.Ano!.Value * 12 + i.Mes!.Value).Distinct().ToList();
        _offsetMax = meses.Count == 0 ? 0 : Math.Max(0, meses.Max() - meses.Min() + 1 - 12);
        if (_offset > _offsetMax) _offset = _offsetMax;
        if (_offset < 0) _offset = 0;
    }

    private void Reconstruir()
    {
        var taxas = Cambio.Todas();
        _itens = Conv.Atual.Propostas.Select(p =>
        {
            int? ano = p.Data?.Year, mes = p.Data?.Month;
            double? tx = ano is { } y && taxas.TryGetValue(y, out var t) ? t : null;
            double? brl = p.ValorUsd is { } v && tx is { } t2 ? v * t2 : null;
            return new Item(p, ano, mes, tx, brl);
        }).ToList();
    }

    private static string AnoTxt(Item i) => i.Ano?.ToString() ?? "Sem data";

    private IEnumerable<Item> Aplicar(string? exceto = null)
    {
        IEnumerable<Item> q = _itens;
        if (exceto != "ano" && _anos.Count > 0) q = q.Where(i => _anos.Contains(AnoTxt(i)));
        if (exceto != "mes" && _meses.Count > 0) q = q.Where(i => i.Mes is { } m && _meses.Contains(Fmt.MonthLong(m)));
        if (exceto != "vend" && _vend.Count > 0) q = q.Where(i => _vend.Contains(i.P.Vendedor));
        if (exceto != "bu" && _bus.Count > 0) q = q.Where(i => _bus.Contains(i.P.Bu));
        if (exceto != "buic" && _buIcs.Count > 0) q = q.Where(i => _buIcs.Contains(i.P.BuIc));
        return q;
    }

    private static Resumo Somar(IEnumerable<Item> itens)
    {
        var r = new Resumo();
        foreach (var i in itens)
        {
            r.Total++;
            var k = (int)i.P.Classe;
            var tem = i.Brl is not null; var b = i.Brl ?? 0;
            switch (i.P.Classe)
            {
                case ClasseProposta.Ganha: r.G++; if (tem) r.VG += b; break;
                case ClasseProposta.Perdida: r.P++; if (tem) r.VP += b; break;
                case ClasseProposta.Aberta: r.A++; if (tem) r.VA += b; break;
                default: r.NC++; if (tem) r.VNC += b; break;
            }
            if (i.P.ValorUsd is null) r.SemValor++;
            else
            {
                r.ComUsd[k]++;
                if (i.Brl is null) { r.SemTaxa++; r.UsdSemTaxa += i.P.ValorUsd.Value; } else r.Conv[k]++;
            }
        }
        return r;
    }

    private static List<(string Nome, Resumo R)> Grupos(IEnumerable<Item> src, Func<Item, string> chave) =>
        src.GroupBy(chave, StringComparer.OrdinalIgnoreCase)
           .Select(g => (Nome: g.Key, R: Somar(g)))
           .OrderByDescending(x => x.R.VTotal).ThenByDescending(x => x.R.Total).ToList();

    // opções em cascata: cada filtro mostra só o que existe com os OUTROS filtros aplicados
    private IEnumerable<string> OpAnos => Aplicar("ano").Select(AnoTxt).Distinct().OrderByDescending(x => x == "Sem data" ? "0" : x);
    private IEnumerable<string> OpMeses => Enumerable.Range(1, 12).Select(Fmt.MonthLong);
    private IEnumerable<string> OpVendedores => Aplicar("vend").Select(i => i.P.Vendedor).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
    private IEnumerable<string> OpBus => Aplicar("bu").Select(i => i.P.Bu).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
    private IEnumerable<string> OpBuIcs => Aplicar("buic").Select(i => i.P.BuIc).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);

    private IEnumerable<string> AnosSemTaxa() =>
        _f.Where(i => i.P.ValorUsd is not null && i.Brl is null).Select(AnoTxt).Distinct().OrderBy(x => x);

    // ---- ciclo de vida -----------------------------------------------------------
    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null)
        {
            var u = (await AuthState).User;
            _isAdmin = u.IsInRole("admin");
            _user = u.Identity?.Name ?? "—";
            _login = u.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? _user;
        }
        _carregando = true;
        try { await Task.Run(() => Conv.Carregar(false)); } finally { _carregando = false; }
        if (_temDados) CarregarOcultas();
    }

    private async Task Atualizar()
    {
        if (_carregando) return;
        _carregando = true;
        try { await Task.Run(() => Conv.Carregar(true)); } finally { _carregando = false; }
    }

    // ---- formatação ----------------------------------------------------------------
    private static string R(double v) => "R$ " + Mon(v);
    /// <summary>Valor com a unidade por extenso: 527,7 mi · 1,06 bi · 850 mil.</summary>
    private static string Mon(double v)
    {
        var a = Math.Abs(v); var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        if (a >= 1_000_000_000) return (v / 1_000_000_000).ToString("0.00", pt) + " bi";
        if (a >= 1_000_000) return (v / 1_000_000).ToString("0.0", pt) + " mi";
        if (a >= 1_000) return Math.Round(v / 1_000).ToString("#,##0", pt) + " mil";
        return v.ToString("#,##0", pt);
    }
    private static string P1(double v) => Fmt.Pct(v, 1);
    private static string N0(int v) => v.ToString("N0", _pt);
    private static string PctDe(double parte, double todo) => todo > 0 ? Fmt.Pct(parte / todo * 100, 1) : "—";
    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    private static string EscTip(string s) => Esc(s).Replace("\n", "&#10;");
    private static string F(double v) => v.ToString("0.##", Inv);
    private static string Pp(double v) => Math.Abs(v).ToString("0.0", _pt) + " p.p.";

    // Estados dos valores: R$ 0 (zero real) ≠ — (sem informação) ≠ Não convertido (sem taxa) ≠ Não calculável.
    private static string Estado(int comUsd, int conv, double v) =>
        comUsd == 0 ? "—" : conv == 0 ? "Não convertido" : R(v) + (conv < comUsd ? "*" : "");
    private static string EstadoTip(int comUsd, int conv, double v) =>
        comUsd == 0 ? "Nenhuma proposta com valor em US$ informado."
        : conv == 0 ? "Há valor em US$, mas falta a taxa de câmbio do ano da proposta (ou a data de envio)."
        : Fmt.Brl(v) + (conv < comUsd ? $" · parcial: {comUsd - conv} proposta(s) sem taxa ficaram de fora." : "");

    private static string Din(Resumo r, ClasseProposta c) => Estado(r.ComUsd[(int)c], r.Conv[(int)c], r.Val(c));
    private static string DinTip(Resumo r, ClasseProposta c) => EstadoTip(r.ComUsd[(int)c], r.Conv[(int)c], r.Val(c));
    private static string DinTotal(Resumo r) => Estado(r.TotComUsd, r.TotConv, r.VTotal);
    private static string DinTotalTip(Resumo r) => EstadoTip(r.TotComUsd, r.TotConv, r.VTotal);

    private string TxQtd(Resumo r) => !_taxaOk ? "—" : r.Decididas == 0 ? "Não calculável" : P1(r.TaxaQtd!.Value);
    private static string TxQtdGlobal(Resumo r) => r.Decididas == 0 ? "Não calculável" : P1(r.TaxaQtd!.Value);
    private string TxValor(Resumo r) => !_taxaOk ? "—" : TxValorGlobal(r);
    private static string TxValorGlobal(Resumo r)
    {
        var usd = r.ComUsd[(int)ClasseProposta.Ganha] + r.ComUsd[(int)ClasseProposta.Perdida];
        var conv = r.Conv[(int)ClasseProposta.Ganha] + r.Conv[(int)ClasseProposta.Perdida];
        if (usd == 0) return "—";
        if (conv == 0) return "Não convertido";
        if (r.VG + r.VP <= 0) return "Não calculável";
        return P1(r.TaxaValor!.Value) + (conv < usd ? "*" : "");
    }

    private static double Largura(double parte, double todo) => todo > 0 ? parte / todo * 100 : 0;

    // variação sobre o período comparável
    private string? DeltaPct(double atual, double? anterior) =>
        anterior is > 0 && _prev is not null ? $"{(atual - anterior.Value) / anterior.Value * 100:+0;-0;0}% vs {int.Parse(_anos.First()) - 1}" : null;

    private (string Texto, string Css) DeltaWin()
    {
        if (_anos.Count != 1 || !int.TryParse(_anos.First(), out var ano))
            return ("", "flat");
        if (_prev is null) return ($"Sem dados comparáveis em {ano - 1}", "flat");
        if (_r.TaxaQtd is not { } cur || _prev.TaxaQtd is not { } ant) return ($"Sem propostas decididas para comparar com {ano - 1}", "flat");
        var d = cur - ant;
        if (Math.Abs(d) < 0.05) return ($"Estável vs {ano - 1} (0,0 p.p.)", "flat");
        return ($"{(d > 0 ? "▲" : "▼")} {Pp(d)} vs {ano - 1} ({P1(ant)})", d > 0 ? "up" : "down");
    }

    private string PeriodoTxt()
    {
        var anos = _anos.Count == 0 ? "Todos os anos" : string.Join(", ", _anos.OrderBy(x => x));
        var meses = _meses.Count == 0 ? "" : " · " + string.Join(", ", _meses.Take(3)) + (_meses.Count > 3 ? $" +{_meses.Count - 3}" : "");
        return anos + meses;
    }

    private string NotasFiltros()
    {
        var l = ConjuntosAtivos().Select(x => $"{x.Item1}: {string.Join(", ", x.Item2.Take(3))}{(x.Item2.Count > 3 ? "…" : "")}").ToList();
        if (_seg is { } s) l.Add("Segmento: " + NomeEn(s));
        return l.Count == 0 ? "Sem filtros adicionais" : string.Join(" · ", l);
    }

    // ======================================================================
    //  EXECUTIVE INSIGHTS · regras determinísticas sobre os dados filtrados
    // ======================================================================
    private sealed record Insight(string Categoria, string Texto, string Aba, string Secao, string Link);

    private const int AmostraMinima = 20;   // decididas para comparar taxas sem ruído

    private List<Insight> Insights()
    {
        var res = new List<Insight>();
        var ano = _anos.Count == 1 && int.TryParse(_anos.First(), out var a0) ? a0 : (int?)null;

        // 1) integridade dos dados — números errados valem mais que qualquer análise
        var dados = new List<string>();
        if (_r.SemTaxa > 0) dados.Add($"{N0(_r.SemTaxa)} proposta(s) (US$ {_r.UsdSemTaxa.ToString("N0", _pt)}) sem taxa de câmbio ficam fora dos valores em R$");
        if (_r.NC > 0) dados.Add($"{N0(_r.NC)} proposta(s) com status não classificado ficam fora de ganhas, perdidas e abertas");
        if (dados.Count > 0) res.Add(new("Qualidade dos dados", string.Join("; ", dados) + ".", "deep", "dados", "Ver validação dos dados"));

        // 2) variação da taxa de ganho
        if (ano is { } y && _prev is not null && _r.Decididas >= AmostraMinima && _prev.Decididas >= AmostraMinima
            && _r.TaxaQtd is { } cur && _prev.TaxaQtd is { } ant && Math.Abs(cur - ant) >= 0.5)
            res.Add(new("Taxa de conversão",
                $"A taxa de ganho {(cur > ant ? "avançou" : "recuou")} {Pp(cur - ant)} em relação a {y - 1} ({P1(ant)} → {P1(cur)}), sobre {N0(_r.Decididas)} propostas decididas.",
                "overview", "", "Ver tendência"));

        // 3) concentração das perdas (status ≠ causa comercial)
        var perdidas = _f.Where(i => i.P.Classe == ClasseProposta.Perdida).ToList();
        if (perdidas.Count >= 10)
        {
            var top = perdidas.GroupBy(i => ConversaoService.NormalizarStatus(i.P.StatusOriginal))
                .Select(g => (Nome: g.First().P.StatusOriginal, Qtd: g.Count())).OrderByDescending(x => x.Qtd).First();
            var share = (double)top.Qtd / perdidas.Count * 100;
            if (share >= 50)
                res.Add(new("Concentração de perdas",
                    $"{top.Nome} representa {P1(share)} das propostas classificadas como perdidas ({N0(top.Qtd)} de {N0(perdidas.Count)}). O status indica a classificação no CRM, não a causa comercial.",
                    "deep", "perdas", "Ver análise de perdas"));
        }

        // 4) diferença de desempenho entre unidades
        var bus = Grupos(_f, i => i.P.Bu).Where(x => x.R.Decididas >= AmostraMinima && x.R.TaxaQtd is not null && x.Nome != ConversaoService.NaoInformado)
            .OrderByDescending(x => x.R.TaxaQtd).ToList();
        if (bus.Count >= 2)
        {
            var hi = bus.First(); var lo = bus.Last();
            var gap = hi.R.TaxaQtd!.Value - lo.R.TaxaQtd!.Value;
            if (gap >= 10)
                res.Add(new("Diferença entre unidades",
                    $"{hi.Nome} converte {P1(hi.R.TaxaQtd.Value)} das propostas decididas ({N0(hi.R.Decididas)}); {lo.Nome}, {P1(lo.R.TaxaQtd.Value)} ({N0(lo.R.Decididas)}) — diferença de {Pp(gap)}.",
                    "deep", "bu", "Comparar unidades"));
        }

        // 5) volume de propostas
        if (_prev is not null && _prev.Total >= AmostraMinima && ano is { } y2)
        {
            var d = ((double)_r.Total - _prev.Total) / _prev.Total * 100;
            if (Math.Abs(d) >= 20)
                res.Add(new("Volume de propostas",
                    $"O volume de propostas enviadas {(d > 0 ? "cresceu" : "recuou")} {Math.Abs(d).ToString("0", _pt)}% frente a {y2 - 1}: {N0(_r.Total)} contra {N0(_prev.Total)}.",
                    "overview", "", "Ver evolução"));
        }

        // 6) exposição em propostas abertas
        if (_r.Conv[(int)ClasseProposta.Aberta] > 0 && _r.VTotal > 0 && _r.A > 0)
            res.Add(new("Pipeline em aberto",
                $"O pipeline aberto soma {R(_r.VA)} em {N0(_r.A)} propostas, {PctDe(_r.VA, _r.VTotal)} do valor total convertido.",
                "deep", "fin", "Ver análise financeira"));

        return res.Take(3).ToList();
    }

    private void IrPara(Insight i)
    {
        var alvo = i.Secao switch { "vend" => "vendedores", "bu" => "unidades", "perdas" => "perdas", "fin" => "receita", "dados" => "qualidade", _ => "evolucao" };
        _ = JS.InvokeVoidAsync("cvIr", alvo);
    }

    // ======================================================================
    //  SÉRIES MENSAIS (mês de ENVIO · status atual — coorte, não fechamento)
    // ======================================================================
    private (List<DateTime> Meses, Dictionary<DateTime, Resumo> Por) Janela(IEnumerable<Item> src, int offset)
    {
        var com = src.Where(i => i.Ano is not null && i.Mes is not null).ToList();
        if (com.Count == 0) return (new List<DateTime>(), new Dictionary<DateTime, Resumo>());
        var todos = com.GroupBy(i => new DateTime(i.Ano!.Value, i.Mes!.Value, 1)).ToDictionary(g => g.Key, g => Somar(g));
        var iniTodos = todos.Keys.Min(); var fim = todos.Keys.Max().AddMonths(-offset);
        if (fim < iniTodos) fim = iniTodos;
        var ini = fim.AddMonths(-11); if (ini < iniTodos) ini = iniTodos;
        var meses = new List<DateTime>();
        for (var m = ini; m <= fim; m = m.AddMonths(1)) meses.Add(m);
        return (meses, todos.Where(kv => kv.Key >= ini && kv.Key <= fim).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>Mesma série, sem o filtro de ano: base do comparativo "mesmo mês do ano anterior".</summary>
    private Dictionary<DateTime, Resumo> SerieBase() =>
        Aplicar("ano").Where(i => i.Ano is not null).GroupBy(i => new DateTime(i.Ano!.Value, i.Mes!.Value, 1)).ToDictionary(g => g.Key, g => Somar(g));

    private static string Vazio(string msg) => $"<div class='empty'>{Esc(msg)}</div>";

    /// <summary>taxa de conversão mensal (linha, eixo 0–100%) + decididas (barras, escala própria, abaixo).</summary>
    private string SvgWinRate(IEnumerable<Item> src, int offset, bool compact, bool comparar = true)
    {
        var (meses, por) = Janela(src, offset);
        if (meses.Count == 0) return Vazio("Nenhuma proposta com data de envio no recorte.");
        // O comparativo "mesmo mês do ano anterior" usa o recorte global; num recorte
        // individual (vendedor) ele compararia coisas diferentes e fica desligado.
        var hist = comparar ? SerieBase() : new Dictionary<DateTime, Resumo>();

        double W = 1000, H = compact ? 250 : 320, L = 46, Rr = 16, T = 14, B = 26, strip = 46, gap = 12;
        var plotH = H - T - B - strip - gap;
        var passo = (W - L - Rr) / meses.Count;
        double X(int i) => L + passo * (i + 0.5);
        double Y(double v) => T + plotH * (1 - v / 100);
        var maxD = Math.Max(meses.Max(m => por.TryGetValue(m, out var rr) ? rr.Decididas : 0), 1);
        var baseStrip = T + plotH + gap + strip;

        var sb = new StringBuilder("<div class='chart-body'>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='taxa de conversão mensal'>");
        foreach (var v in new[] { 0, 25, 50, 75, 100 })
        {
            sb.Append($"<line x1='{F(L)}' y1='{F(Y(v))}' x2='{F(W - Rr)}' y2='{F(Y(v))}' stroke='#e8edf4'/>");
            sb.Append($"<text x='{F(L - 8)}' y='{F(Y(v) + 3)}' text-anchor='end' font-size='10.5' fill='#6b7a90'>{v}%</text>");
        }

        // linha do ano anterior (tracejada) e linha atual, quebrando onde não há propostas decididas
        string Caminho(Func<DateTime, double?> valor)
        {
            var d = new StringBuilder(); var aberto = false;
            for (var i = 0; i < meses.Count; i++)
            {
                var v = valor(meses[i]);
                if (v is null) { aberto = false; continue; }
                d.Append(aberto ? " L " : " M ").Append(F(X(i))).Append(',').Append(F(Y(v.Value)));
                aberto = true;
            }
            return d.ToString().Trim();
        }
        double? Taxa(Dictionary<DateTime, Resumo> dic, DateTime m) => dic.TryGetValue(m, out var rr) && rr.Decididas > 0 ? rr.TaxaQtd : null;

        var anterior = Caminho(m => Taxa(hist, m.AddYears(-1)));
        if (anterior != "") sb.Append($"<path d='{anterior}' fill='none' stroke='#93a1b5' stroke-width='1.6' stroke-dasharray='5 4'/>");
        var atual = Caminho(m => Taxa(por, m));
        if (atual != "") sb.Append($"<path d='{atual}' fill='none' stroke='{CorA}' stroke-width='2.6' stroke-linejoin='round' stroke-linecap='round'/>");

        for (var i = 0; i < meses.Count; i++)
        {
            por.TryGetValue(meses[i], out var r);
            var x = X(i);
            if (r is not null && r.Decididas > 0)
            {
                var h = r.Decididas / (double)maxD * strip;
                sb.Append($"<rect x='{F(x - Math.Min(passo * 0.28, 18))}' y='{F(baseStrip - h)}' width='{F(Math.Min(passo * 0.56, 36))}' height='{F(h)}' rx='2' fill='#C9D6E4'/>");
                var abertas = r.Total > 0 ? (double)r.A / r.Total : 0;
                var imaturo = abertas > 0.30;
                sb.Append($"<circle cx='{F(x)}' cy='{F(Y(r.TaxaQtd!.Value))}' r='4.5' fill='{(imaturo ? "#FFFFFF" : CorA)}' stroke='{CorA}' stroke-width='2'/>");
            }
            hist.TryGetValue(meses[i].AddYears(-1), out var ra);
            var tip = $"{meses[i].ToString("MMMM/yyyy", _pt)}\n" +
                      (r is null ? "Sem propostas enviadas" :
                       $"taxa de conversão: {(r.Decididas > 0 ? P1(r.TaxaQtd!.Value) : "não calculável")} ({N0(r.G)} ganhas de {N0(r.Decididas)} decididas)\n" +
                       $"Perdidas: {N0(r.P)} · Abertas: {N0(r.A)} ({PctDe(r.A, r.Total)} do mês)" +
                       (r.NC > 0 ? $" · Não classificadas: {N0(r.NC)}" : "")) +
                      (ra is not null && ra.Decididas > 0 ? $"\nMesmo mês de {meses[i].Year - 1}: {P1(ra.TaxaQtd!.Value)} ({N0(ra.Decididas)} decididas)" : "");
            sb.Append($"<rect x='{F(L + passo * i)}' y='{F(T)}' width='{F(passo)}' height='{F(H - T - B + 6)}' fill='transparent'><title>{EscTip(tip)}</title></rect>");
            sb.Append($"<text x='{F(x)}' y='{F(H - 8)}' text-anchor='middle' font-size='10.5' fill='#3b4a61'>{Esc(meses[i].ToString("MMM/yy", _pt))}</text>");
        }
        sb.Append($"<text x='{F(L - 8)}' y='{F(baseStrip - strip + 6)}' text-anchor='end' font-size='9.5' fill='#6b7a90'>{maxD}</text>");
        sb.Append($"<text x='{F(L - 8)}' y='{F(baseStrip)}' text-anchor='end' font-size='9.5' fill='#6b7a90'>0</text>");
        sb.Append("</svg>");
        sb.Append("<div class='cvx-leg'>");
        sb.Append($"<span><i class='cvx-ln' style='background:{CorA}'></i>Taxa de conversão do mês</span>");
        if (comparar) sb.Append("<span><i class='cvx-ln dash'></i>Mesmo mês do ano anterior</span>");
        sb.Append($"<span><i class='cvx-pt' style='border-color:{CorA}'></i>Mais de 30% das propostas do mês ainda abertas (a taxa pode mudar)</span>");
        sb.Append("<span><i class='cvx-bx'></i>Propostas decididas (escala própria, embaixo)</span>");
        sb.Append("</div></div>");
        return sb.ToString();
    }

    /// <summary>Colunas empilhadas por mês: quantidade ou valor (R$), sempre partindo do zero.</summary>
    private string SvgEmpilhado(IEnumerable<Item> src, int offset, bool qtd)
    {
        var (meses, por) = Janela(src, offset);
        if (meses.Count == 0) return Vazio("Nenhuma proposta com data de envio no recorte.");

        double Val(Resumo r, int c) => qtd
            ? (c == 0 ? r.G : c == 1 ? r.P : c == 2 ? r.A : r.NC)
            : (c == 0 ? r.VG : c == 1 ? r.VP : c == 2 ? r.VA : r.VNC);
        var temNc = por.Values.Any(r => r.NC > 0);
        var cats = temNc ? 4 : 3;
        double maxY = 1;
        foreach (var r in por.Values) maxY = Math.Max(maxY, Enumerable.Range(0, cats).Sum(c => Val(r, c)));

        const double W = 1000, H = 300, L = 56, Rr = 14, T = 14, B = 30;
        var passo = (W - L - Rr) / meses.Count; var larg = Math.Min(passo * 0.62, 46);
        double Y(double v) => T + (H - T - B) * (1 - v / maxY);
        string Rot(double v) => qtd ? F(Math.Round(v)) : R(v);

        var sb = new StringBuilder("<div class='chart-body'>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='{(qtd ? "Volume" : "Valor")} mensal'>");
        for (var k = 0; k <= 4; k++)
        {
            var v = maxY * k / 4;
            sb.Append($"<line x1='{F(L)}' y1='{F(Y(v))}' x2='{F(W - Rr)}' y2='{F(Y(v))}' stroke='#e8edf4'/>");
            sb.Append($"<text x='{F(L - 8)}' y='{F(Y(v) + 3)}' text-anchor='end' font-size='10.5' fill='#6b7a90'>{Esc(Rot(v))}</text>");
        }
        for (var i = 0; i < meses.Count; i++)
        {
            var x = L + passo * i + (passo - larg) / 2;
            if (por.TryGetValue(meses[i], out var r))
            {
                double acum = 0;
                for (var c = 0; c < cats; c++)
                {
                    var v = Val(r, c); if (v <= 0) continue;
                    var cls = Classes[c];
                    var tip = $"{NomeEn(cls)} em {meses[i].ToString("MMM/yyyy", _pt)}\n{N0(r.Qtd(cls))} proposta(s) · {Din(r, cls)}";
                    sb.Append($"<rect class='cv-seg' x='{F(x)}' y='{F(Y(acum + v))}' width='{F(larg)}' height='{F(Y(acum) - Y(acum + v))}' fill='{Cor(cls)}' rx='2'><title>{EscTip(tip)}</title></rect>");
                    acum += v;
                }
            }
            sb.Append($"<text x='{F(x + larg / 2)}' y='{F(H - 10)}' text-anchor='middle' font-size='10.5' fill='#3b4a61'>{Esc(meses[i].ToString("MMM/yy", _pt))}</text>");
        }
        sb.Append("</svg><div class='cvx-leg'>");
        for (var c = 0; c < cats; c++) sb.Append($"<span><i class='cvx-dot' style='background:{Cor(Classes[c])}'></i>{NomeEn(Classes[c])}</span>");
        sb.Append("</div></div>");
        return sb.ToString();
    }

    private string _metTrend = "winrate";    // winrate | volume | valor
    private RenderFragment Tendencia(int offset, bool compact = false) => builder =>
    {
        string html;
        if (_metTrend == "winrate" && !_taxaOk)
            html = Vazio("taxa de conversão não se aplica com um segmento selecionado na barra de composição. Remova o segmento para ver a taxa.");
        else html = SvgAnual(_metTrend == "winrate" ? _f : _fx, _metTrend);
        builder.AddMarkupContent(0, html);
    };

    /// <summary>Uma linha por ano de envio, janeiro a dezembro. A métrica (taxa, quantidade ou valor) vem do botão do topo; nunca mistura escalas.</summary>
    private string SvgAnual(IEnumerable<Item> src, string met)
    {
        var com = src.Where(i => i.Ano is not null && i.Mes is not null).ToList();
        if (com.Count == 0) return Vazio("Nenhuma proposta com data de envio no recorte.");
        var por = com.GroupBy(i => (A: i.Ano!.Value, M: i.Mes!.Value)).ToDictionary(g => g.Key, g => Somar(g));
        var anos = por.Keys.Select(k => k.A).Distinct().OrderByDescending(a => a).ToList();
        double? Val(int a, int m)
        {
            if (!por.TryGetValue((a, m), out var r)) return null;
            return met == "winrate" ? (r.Decididas > 0 ? r.TaxaQtd : null) : met == "volume" ? r.Total : (r.TotConv > 0 ? r.VTotal : null);
        }
        string Rot(double v) => met == "winrate" ? F(v) + "%" : met == "volume" ? N0((int)Math.Round(v)) : Mon(v);
        string Txt(int a, int m)
        {
            if (!por.TryGetValue((a, m), out var r)) return "sem propostas";
            return met == "winrate" ? (r.Decididas > 0 ? $"{P1(r.TaxaQtd!.Value)} ({N0(r.G)} ganhas de {N0(r.Decididas)} decididas)" : "não calculável (sem propostas decididas)")
                 : met == "volume" ? $"{N0(r.Total)} propostas · {N0(r.G)} ganhas, {N0(r.P)} perdidas, {N0(r.A)} abertas"
                 : (r.TotConv > 0 ? $"{R(r.VTotal)} · ganho {Din(r, ClasseProposta.Ganha)}, perdido {Din(r, ClasseProposta.Perdida)}, aberto {Din(r, ClasseProposta.Aberta)}" : "valor não convertido (sem taxa de câmbio)");
        }

        var maxV = 0.0;
        foreach (var a in anos) for (var m = 1; m <= 12; m++) maxV = Math.Max(maxV, Val(a, m) ?? 0);
        double Nice(double v)
        {
            if (v <= 0) return 1;
            var p = Math.Pow(10, Math.Floor(Math.Log10(v))); var f = v / p;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p;
        }
        var maxY = met == "winrate" ? 100 : Nice(maxV);

        string[] cores = { CorA, CorP, CorG, "#6b4fa3", "#0b4f6f", CorN };
        var mesAtual = Enumerable.Range(1, 12).Where(m => Val(anos[0], m) is not null).DefaultIfEmpty(0).Max();
        double W = 1100, H = 340, L = 74, Rr = 24, T = 26, B = 36;
        var passo = (W - L - Rr) / 12;
        double X(int m) => L + passo * (m - 0.5);
        double Y(double v) => T + (H - T - B) * (1 - v / maxY);

        var sb = new StringBuilder("<div class='chart-body cvx-anual'><div class='cvx-leg top'>");
        for (var k = 0; k < anos.Count; k++)
            sb.Append($"<span><i class='cvx-ln {(k == 0 ? "" : "dash")}' style='border-top-color:{cores[k % cores.Length]}'></i>{anos[k]}</span>");
        sb.Append("</div>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='evolução mensal por ano'><defs><linearGradient id='cvArea' x1='0' y1='0' x2='0' y2='1'><stop offset='0%' stop-color='{CorA}' stop-opacity='.22'/><stop offset='100%' stop-color='{CorA}' stop-opacity='0'/></linearGradient></defs>");
        for (var g = 0; g <= 4; g++)
        {
            var v = maxY * g / 4;
            sb.Append($"<line x1='{F(L)}' y1='{F(Y(v))}' x2='{F(W - Rr)}' y2='{F(Y(v))}' stroke='#eef2f7'/>");
            sb.Append($"<text x='{F(L - 12)}' y='{F(Y(v) + 4)}' text-anchor='end' font-size='13' fill='#93a1b5'>{Esc(g == 0 ? "0" : Rot(v))}</text>");
        }
        for (var m = 1; m <= 12; m++)
            sb.Append($"<text x='{F(X(m))}' y='{F(H - 10)}' text-anchor='middle' font-size='13' font-weight='{(m == mesAtual ? 700 : 500)}' fill='{(m == mesAtual ? CorA : "#93a1b5")}' letter-spacing='.04em'>{Esc(new DateTime(2000, m, 1).ToString("MMM", _pt).TrimEnd('.').ToUpper(_pt))}</text>");

        // anos antigos primeiro, para o mais recente ficar por cima
        for (var k = anos.Count - 1; k >= 0; k--)
        {
            var a = anos[k]; var cor = cores[k % cores.Length]; var atual = k == 0;
            var d = new StringBuilder(); var area = new StringBuilder();
            var seg = new List<(double X, double Y)>();
            void FechaSeg()
            {
                if (atual && seg.Count > 1)
                {
                    area.Append("M ").Append(F(seg[0].X)).Append(',').Append(F(Y(0)));
                    foreach (var p in seg) area.Append(" L ").Append(F(p.X)).Append(',').Append(F(p.Y));
                    area.Append(" L ").Append(F(seg[^1].X)).Append(',').Append(F(Y(0))).Append(" Z ");
                }
                seg.Clear();
            }
            for (var m = 1; m <= 12; m++)
            {
                var v = Val(a, m);
                if (v is null) { FechaSeg(); continue; }
                d.Append(seg.Count > 0 ? " L " : " M ").Append(F(X(m))).Append(',').Append(F(Y(v.Value)));
                seg.Add((X(m), Y(v.Value)));
            }
            FechaSeg();
            if (area.Length > 0) sb.Append($"<path d='{area.ToString().Trim()}' fill='url(#cvArea)' stroke='none'/>");
            if (d.Length > 0)
                sb.Append($"<path d='{d.ToString().Trim()}' fill='none' stroke='{cor}' stroke-width='{(atual ? 3.2 : 2.4)}' stroke-linejoin='round' stroke-linecap='round'{(atual ? "" : " stroke-dasharray='7 5'")}/>");
            for (var m = 1; m <= 12; m++)
            {
                var v = Val(a, m); if (v is null) continue;
                sb.Append($"<circle cx='{F(X(m))}' cy='{F(Y(v.Value))}' r='{(atual ? 5.2 : 3.8)}' fill='#fff' stroke='{cor}' stroke-width='2.4'><title>{EscTip($"{a} · {new DateTime(2000, m, 1).ToString("MMMM", _pt)}\n{Txt(a, m)}")}</title></circle>");
                if (atual) sb.Append($"<text x='{F(X(m))}' y='{F(Y(v.Value) - 12)}' text-anchor='middle' font-size='12.5' font-weight='600' fill='#3b4a61'>{Esc(Rot(v.Value))}</text>");
            }
        }
        // faixa por mês: tooltip com todos os anos lado a lado
        for (var m = 1; m <= 12; m++)
        {
            var linhas = string.Join("\n", anos.Select(a => $"{a}: {Txt(a, m)}"));
            sb.Append($"<rect x='{F(L + passo * (m - 1))}' y='{F(T)}' width='{F(passo)}' height='{F(H - T - B)}' fill='transparent'><title>{EscTip(new DateTime(2000, m, 1).ToString("MMMM", _pt) + "\n" + linhas)}</title></rect>");
        }
        sb.Append("</svg></div>");
        return sb.ToString();
    }
    private RenderFragment Html(string html) => builder => builder.AddMarkupContent(0, html);

    // ======================================================================
    //  BLOCOS HTML ESTÁTICOS (usados também no relatório)
    // ======================================================================
    private string HtmlComposicao(Resumo r)
    {
        var sb = new StringBuilder("<div class='cvx-stackbar static'>");
        foreach (var c in Classes)
        {
            var n = r.Qtd(c); if (n == 0) continue;
            var pct = Largura(n, r.Total);
            sb.Append($"<span class='cvx-seg' style='width:{F(pct)}%;background:{Cor(c)}' title='{EscTip($"{NomeEn(c)}: {N0(n)} ({PctDe(n, r.Total)})\n{DefClasse(c)}")}'>{(pct >= 8 ? Esc(P1(pct)) : "")}</span>");
        }
        sb.Append("</div><div class='cvx-legend'>");
        foreach (var c in Classes)
        {
            if (c == ClasseProposta.NaoClassificada && r.NC == 0) continue;
            sb.Append($"<span><i class='cvx-dot' style='background:{Cor(c)}'></i>{NomeEn(c)} <b>{N0(r.Qtd(c))}</b> · {PctDe(r.Qtd(c), r.Total)}</span>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static readonly string[] StatusPerda = { "Cancelled", "Closed - Customer Lost", "Closed - Lost", "Closed - No Bid", "Closed - Unknown" };

    private List<(string Nome, int Qtd, double Valor, int ComUsd, int Conv)> LinhasPareto(bool qtd)
    {
        var perdidas = _f.Where(i => i.P.Classe == ClasseProposta.Perdida).ToList();
        var l = StatusPerda.Select(s =>
        {
            var chave = ConversaoService.NormalizarStatus(s);
            var its = perdidas.Where(i => ConversaoService.NormalizarStatus(i.P.StatusOriginal) == chave).ToList();
            return (Nome: s, Qtd: its.Count, Valor: its.Sum(i => i.Brl ?? 0),
                    ComUsd: its.Count(i => i.P.ValorUsd is not null), Conv: its.Count(i => i.Brl is not null));
        });
        return (qtd ? l.OrderByDescending(x => x.Qtd).ThenByDescending(x => x.Valor) : l.OrderByDescending(x => x.Valor).ThenByDescending(x => x.Qtd)).ToList();
    }

    private string _metPerda = "qtd";
    /// <summary>Pareto horizontal: barra por status, participação e % acumulado.</summary>
    private string HtmlPareto(bool qtd)
    {
        var linhas = LinhasPareto(qtd);
        var totQ = linhas.Sum(x => x.Qtd); var totV = linhas.Sum(x => x.Valor);
        if (totQ == 0) return Vazio("Nenhuma proposta perdida no recorte.");
        var max = Math.Max(linhas.Max(x => qtd ? x.Qtd : x.Valor), 0.0000001);
        var total = qtd ? totQ : totV;
        var sb = new StringBuilder("<div class='cvx-pareto'>");
        sb.Append("<div class='cvx-prow head'><span>Status no CRM</span><span></span><span class='r'>" + (qtd ? "Propostas" : "Valor") + "</span><span class='r'>Participação</span><span class='r'>Acumulado</span></div>");
        double acum = 0;
        foreach (var l in linhas)
        {
            var v = qtd ? l.Qtd : l.Valor; acum += v;
            var valorTxt = qtd ? N0(l.Qtd) : Estado(l.ComUsd, l.Conv, l.Valor);
            var tip = $"{l.Nome}\n{N0(l.Qtd)} proposta(s) · {EstadoTip(l.ComUsd, l.Conv, l.Valor)}";
            sb.Append($"<div class='cvx-prow' title='{EscTip(tip)}'><span class='nm'>{Esc(l.Nome)}</span>");
            sb.Append($"<span class='bar'><i style='width:{F(v / max * 100)}%;background:{CorP}'></i></span>");
            sb.Append($"<span class='r'><b>{Esc(valorTxt)}</b></span><span class='r'>{Esc(PctDe(v, total))}</span><span class='r muted'>{Esc(PctDe(acum, total))}</span></div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private string TextoInsightPerda()
    {
        var linhas = LinhasPareto(true);
        var tot = linhas.Sum(x => x.Qtd);
        if (tot == 0) return "Sem propostas perdidas no recorte atual.";
        var top = linhas.First();
        var totV = linhas.Sum(x => x.Valor);
        var s = $"{top.Nome} representa {PctDe(top.Qtd, tot)} das propostas classificadas como perdidas ({N0(top.Qtd)} de {N0(tot)}).";
        var topV = LinhasPareto(false).First();
        if (totV > 0 && topV.Nome != top.Nome) s += $" Em valor, o maior impacto é de {topV.Nome} ({PctDe(topV.Valor, totV)} do valor perdido convertido).";
        else if (totV > 0) s += $" Em valor, também lidera, com {PctDe(topV.Valor, totV)} do valor perdido convertido.";
        return s;
    }

    private string HtmlVendTop(int n, int minDec)
    {
        var l = Grupos(_f, i => i.P.Vendedor).Where(x => x.R.Decididas >= minDec && x.R.TaxaQtd is not null)
            .OrderByDescending(x => x.R.TaxaQtd).ThenByDescending(x => x.R.Decididas).Take(n).ToList();
        if (l.Count == 0) return Vazio($"Nenhum vendedor com {minDec}+ propostas decididas no recorte.");
        var sb = new StringBuilder("<table class='cvx-mini-tbl'><thead><tr><th>#</th><th>Vendedor</th><th class='r'>taxa de conversão</th><th class='r'>Ganhas</th><th class='r'>Perdidas</th><th class='r'>Abertas</th><th class='r'>Decididas</th><th class='r'>Valor ganho</th></tr></thead><tbody>");
        for (var i = 0; i < l.Count; i++)
        {
            var (nome, r) = l[i];
            sb.Append($"<tr><td>{i + 1}</td><td><b>{Esc(nome)}</b></td><td class='r'><b>{Esc(TxQtdGlobal(r))}</b></td><td class='r'>{N0(r.G)}</td><td class='r'>{N0(r.P)}</td><td class='r'>{N0(r.A)}</td><td class='r'>{N0(r.Decididas)}</td><td class='r' title='{EscTip(DinTip(r, ClasseProposta.Ganha))}'>{Esc(Din(r, ClasseProposta.Ganha))}</td></tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private string HtmlBuBars(List<(string Nome, Resumo R)> grupos, int max)
    {
        var l = grupos.Where(x => x.R.Decididas > 0).OrderByDescending(x => x.R.TaxaQtd).ThenByDescending(x => x.R.Decididas).Take(max).ToList();
        if (l.Count == 0) return Vazio("Sem propostas decididas no recorte.");
        var sb = new StringBuilder("<div class='cvx-bubars'>");
        foreach (var (nome, r) in l)
            sb.Append($"<div class='cvx-bb' title='{EscTip($"{nome}\n{N0(r.G)} ganhas de {N0(r.Decididas)} decididas")}'><span class='nm'>{Esc(nome)}</span><span class='bar'><i style='width:{F(r.TaxaQtd!.Value)}%;background:{CorA}'></i></span><b>{Esc(P1(r.TaxaQtd.Value))}</b><small>{N0(r.Decididas)} decididas</small></div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Distribuição financeira: barra de composição por valor (só do que foi convertido).</summary>
    private string HtmlComposicaoValor(Resumo r)
    {
        if (r.TotConv == 0) return Vazio(r.TotComUsd == 0 ? "Nenhuma proposta com valor em US$ informado." : "Valores não convertidos: falta a taxa de câmbio do ano das propostas.");
        var tot = Math.Max(r.VTotal, 0.0000001);
        var sb = new StringBuilder("<div class='cvx-stackbar static'>");
        foreach (var c in Classes)
        {
            var v = r.Val(c); if (v <= 0) continue;
            var pct = v / tot * 100;
            sb.Append($"<span class='cvx-seg' style='width:{F(pct)}%;background:{Cor(c)}' title='{EscTip($"{NomeEn(c)}: {DinTip(r, c)}")}'>{(pct >= 9 ? Esc(P1(pct)) : "")}</span>");
        }
        sb.Append("</div><div class='cvx-legend'>");
        foreach (var c in Classes)
        {
            if (c == ClasseProposta.NaoClassificada && r.VNC <= 0) continue;
            sb.Append($"<span title='{EscTip(DinTip(r, c))}'><i class='cvx-dot' style='background:{Cor(c)}'></i>{NomeEn(c)} <b>{Esc(Din(r, c))}</b> · {PctDe(r.Val(c), r.VTotal)}</span>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Valor ganho/perdido/aberto por unidade, em barras empilhadas partindo do zero.</summary>
    private string HtmlValorPorUnidade(List<(string Nome, Resumo R)> grupos)
    {
        var l = grupos.Where(x => x.R.TotConv > 0).OrderByDescending(x => x.R.VTotal).Take(10).ToList();
        if (l.Count == 0) return Vazio("Sem valores convertidos por unidade no recorte.");
        var max = Math.Max(l.Max(x => x.R.VTotal), 0.0000001);
        var sb = new StringBuilder("<div class='cvx-bubars'>");
        foreach (var (nome, r) in l)
        {
            sb.Append($"<div class='cvx-bb v' title='{EscTip($"{nome}\nGanho: {Din(r, ClasseProposta.Ganha)}\nPerdido: {Din(r, ClasseProposta.Perdida)}\nAberto: {Din(r, ClasseProposta.Aberta)}")}'><span class='nm'>{Esc(nome)}</span><span class='bar stack' style='width:{F(r.VTotal / max * 100)}%'>");
            foreach (var c in Classes) if (r.Val(c) > 0) sb.Append($"<i style='flex:{F(r.Val(c))};background:{Cor(c)}'></i>");
            sb.Append($"</span><b>{Esc(R(r.VTotal))}</b></div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ======================================================================
    //  VENDEDORES · ranking com volume mínimo
    // ======================================================================
    private string _ordVend = "wr";
    private int _minDec = 5;
    private string? _vendSel;

    private List<(string Nome, Resumo R)> VendLista(out int ocultos)
    {
        var tudo = Grupos(_fx, i => i.P.Vendedor);
        var vis = tudo.Where(x => x.R.Decididas >= _minDec || !_taxaOk).ToList();
        ocultos = tudo.Count - vis.Count;
        IEnumerable<(string Nome, Resumo R)> ord = _ordVend switch
        {
            "valor" => vis.OrderByDescending(x => x.R.VG).ThenByDescending(x => x.R.G),
            "ganhas" => vis.OrderByDescending(x => x.R.G).ThenByDescending(x => x.R.VG),
            "decididas" => vis.OrderByDescending(x => x.R.Decididas).ThenByDescending(x => x.R.G),
            "perdidas" => vis.OrderByDescending(x => x.R.P).ThenByDescending(x => x.R.Decididas),
            _ => vis.OrderByDescending(x => x.R.TaxaQtd ?? -1).ThenByDescending(x => x.R.Decididas),
        };
        return ord.ToList();
    }

    private void SetMinDec(ChangeEventArgs e) => _minDec = int.TryParse(e.Value?.ToString(), out var v) && v >= 0 ? v : 0;
    private void AbrirVendedor(string nome) => _vendSel = nome;

    // ======================================================================
    //  UNIDADES (BU e BU Intercompany)
    // ======================================================================

    private List<(string Nome, Resumo R)> UnidadesOrdenadas(Func<Item, string> sel) =>
        Grupos(_fx, sel).OrderByDescending(x => x.R.Decididas > 0 && _taxaOk ? x.R.TaxaQtd : -1)
                        .ThenByDescending(x => x.R.Decididas).ThenByDescending(x => x.R.VTotal).ToList();

    // ======================================================================
    //  BASE DE PROPOSTAS (mesmo recorte _f)
    // ======================================================================
    private string _busca = "";
    private readonly HashSet<string> _classes = Novo();
    private string _ordCol = "data"; private bool _ordAsc = false;
    private int _pag; private const int PorPagina = 50;
    private HashSet<string>? _ocultas;
    private const string TabelaPref = "conversao_base";
    private static readonly string[] NomesClasse = { "Ganha", "Perdida", "Aberta", "Não classificada" };

    private sealed record Col(string Key, string Titulo, string Align, Func<Item, string> Texto, Func<Item, object?> Chave);

    private List<Col> Colunas()
    {
        var cols = new List<Col>
        {
            new("data", "Data de envio", "nowrap", i => i.P.Data?.ToString("dd/MM/yyyy") ?? (i.P.DataTexto == "" ? "—" : i.P.DataTexto + " (inválida)"), i => i.P.Data),
            new("ano", "Ano", "center", i => i.Ano?.ToString() ?? "—", i => i.Ano),
            new("mes", "Mês", "center", i => i.Mes is { } m ? Fmt.MonthLong(m) : "—", i => i.Mes),
            new("vend", "Vendedor", "", i => i.P.Vendedor, i => i.P.Vendedor),
            new("bu", "BU", "", i => i.P.Bu, i => i.P.Bu),
            new("buic", "BU Intercompany", "", i => i.P.BuIc, i => i.P.BuIc),
            new("status", "Status original", "", i => i.P.StatusOriginal == "" ? "—" : i.P.StatusOriginal, i => i.P.StatusOriginal),
            new("classe", "Classificação", "center", i => NomeClasse(i.P.Classe), i => (int)i.P.Classe),
            new("usd", "Valor original (US$)", "right", i => i.P.ValorUsd is { } v ? v.ToString("#,##0.00", _pt) : (i.P.ValorTexto == "" ? "—" : i.P.ValorTexto + " (ilegível)"), i => i.P.ValorUsd),
            new("taxa", "Taxa aplicada", "right", i => i.Taxa is { } t ? t.ToString("0.0000", _pt) : "sem taxa", i => i.Taxa),
            new("brl", "Valor convertido (R$)", "right", i => i.Brl is { } b ? b.ToString("#,##0.00", _pt) : (i.P.ValorUsd is null ? "—" : "não convertido"), i => i.Brl),
            new("linha", "Linha na planilha", "center", i => i.P.Linha.ToString(), i => i.P.Linha),
        };
        foreach (var c in Conv.Atual.Colunas)
        {
            var idx = c.Indice;
            cols.Add(new Col("o" + idx, $"{c.Letra} · {c.Titulo}", "", i => idx < i.P.Celulas.Length && i.P.Celulas[idx] != "" ? i.P.Celulas[idx] : "—",
                i => idx < i.P.Celulas.Length ? i.P.Celulas[idx] : ""));
        }
        return cols;
    }

    // colunas originais da planilha começam ocultas; a escolha do usuário fica guardada
    private void CarregarOcultas()
    {
        var pref = ColPrefs.Get(_login, TabelaPref);
        _ocultas = pref.StartsWith("v1:", StringComparison.Ordinal)
            ? new HashSet<string>(pref[3..].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal)
            : new HashSet<string>(Conv.Atual.Colunas.Select(c => "o" + c.Indice), StringComparer.Ordinal);
    }
    private void AlternarColuna(string key)
    {
        _ocultas ??= new HashSet<string>(StringComparer.Ordinal);
        if (!_ocultas.Remove(key)) _ocultas.Add(key);
        ColPrefs.Save(_login, TabelaPref, "v1:" + string.Join(",", _ocultas));
    }

    private static int Cmp(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        return (a, b) switch
        {
            (double x, double y) => x.CompareTo(y),
            (int x, int y) => x.CompareTo(y),
            (DateTime x, DateTime y) => x.CompareTo(y),
            _ => string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase),
        };
    }

    private void Ordenar(string key)
    {
        if (_ordCol == key) _ordAsc = !_ordAsc; else { _ordCol = key; _ordAsc = true; }
        _pag = 0;
    }
    private void Buscar(ChangeEventArgs e) { _busca = e.Value?.ToString() ?? ""; _pag = 0; }

    private List<Item> LinhasTabela(List<Col> cols)
    {
        IEnumerable<Item> q = _f;                                         // o MESMO recorte global
        if (_classes.Count > 0) q = q.Where(i => _classes.Contains(NomeClasse(i.P.Classe)));
        if (!string.IsNullOrWhiteSpace(_busca))
        {
            var b = _busca.Trim();
            q = q.Where(i => cols.Any(c => c.Texto(i).Contains(b, StringComparison.CurrentCultureIgnoreCase)));
        }
        var col = cols.FirstOrDefault(c => c.Key == _ordCol) ?? cols[0];
        q = _ordAsc ? q.OrderBy(col.Chave, Comparer<object?>.Create(Cmp)) : q.OrderByDescending(col.Chave, Comparer<object?>.Create(Cmp));
        return q.ToList();
    }

    private async Task Exportar(List<Item> linhas, List<Col> todas)
    {
        if (linhas.Count == 0) return;
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Base de Propostas");
        for (var c = 0; c < todas.Count; c++) ws.Cell(1, c + 1).Value = todas[c].Titulo;
        ws.Row(1).Style.Font.Bold = true;
        for (var r = 0; r < linhas.Count; r++)
        {
            var i = linhas[r];
            for (var c = 0; c < todas.Count; c++)
            {
                var cel = ws.Cell(r + 2, c + 1);
                switch (todas[c].Key)
                {
                    case "usd": if (i.P.ValorUsd is { } u) { cel.Value = u; cel.Style.NumberFormat.Format = "#,##0.00"; } break;
                    case "taxa": if (i.Taxa is { } t) { cel.Value = t; cel.Style.NumberFormat.Format = "0.0000"; } else cel.Value = "sem taxa"; break;
                    case "brl": if (i.Brl is { } b) { cel.Value = b; cel.Style.NumberFormat.Format = "#,##0.00"; } else if (i.P.ValorUsd is not null) cel.Value = "não convertido"; break;
                    case "ano": if (i.Ano is { } a) cel.Value = a; break;
                    case "linha": cel.Value = i.P.Linha; break;
                    case "data": if (i.P.Data is { } d) { cel.Value = d; cel.Style.DateFormat.Format = "dd/MM/yyyy"; } break;
                    default: cel.Value = todas[c].Texto(i) == "—" ? "" : todas[c].Texto(i); break;
                }
            }
        }
        ws.SheetView.FreezeRows(1);
        ws.RangeUsed()?.SetAutoFilter();
        ws.Columns(1, Math.Min(todas.Count, 12)).AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        await JS.InvokeVoidAsync("appDownload", $"base_de_propostas_{DateTime.Today:yyyy-MM-dd}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Convert.ToBase64String(ms.ToArray()));
    }

    // ======================================================================
    //  RELATÓRIO EXECUTIVO (impressão / PDF)
    // ======================================================================
    private bool _relatorio;
    private async Task AbrirRelatorio()
    {
        _relatorio = true;
        try { await JS.InvokeVoidAsync("cvRelatorio", true); } catch { }
    }
    private async Task FecharRelatorio()
    {
        _relatorio = false;
        try { await JS.InvokeVoidAsync("cvRelatorio", false); } catch { }
    }
    private async Task Imprimir()
    {
        try { await JS.InvokeVoidAsync("appPrint"); } catch { }
    }

    // ======================================================================
    //  TAXAS DE CÂMBIO (administrador)
    // ======================================================================
    private bool _taxasAberto;
    private string _taxasErro = "", _novoAno = "";
    private readonly Dictionary<int, string> _taxasTxt = new();
    private List<int> _taxasAnos = new();

    private void SetTaxa(int ano, ChangeEventArgs e) => _taxasTxt[ano] = e.Value?.ToString() ?? "";

    private void AbrirTaxas()
    {
        if (!_isAdmin) return;
        _taxasErro = ""; _novoAno = "";
        var salvas = Cambio.Todas();
        var maximo = new[] { DateTime.Today.Year, salvas.Count > 0 ? salvas.Keys.Max() : 0,
            Conv.Atual.Propostas.Where(p => p.Data is not null).Select(p => p.Data!.Value.Year).DefaultIfEmpty(0).Max() }.Max();
        _taxasAnos = Enumerable.Range(2024, Math.Max(maximo - 2024 + 1, 1)).Union(salvas.Keys).Distinct().OrderBy(x => x).ToList();
        _taxasTxt.Clear();
        foreach (var a in _taxasAnos) _taxasTxt[a] = salvas.TryGetValue(a, out var t) ? t.ToString("0.0000", _pt) : "";
        _taxasAberto = true;
    }

    private void AdicionarAno()
    {
        if (int.TryParse(_novoAno.Trim(), out var a) && a is >= 2000 and <= 2100)
        {
            if (!_taxasAnos.Contains(a)) { _taxasAnos.Add(a); _taxasAnos.Sort(); _taxasTxt[a] = ""; }
            _novoAno = ""; _taxasErro = "";
        }
        else _taxasErro = "Informe um ano válido (ex.: 2027).";
    }

    // "5,20" ou "5.20": o separador decimal é o último; nunca "5.20" lido como 520.
    private static double? LerTaxa(string txt)
    {
        var t = (txt ?? "").Trim().Replace(" ", "");
        if (t == "") return null;
        if (t.Contains(',')) t = t.Replace(".", "").Replace(',', '.');
        return double.TryParse(t, System.Globalization.NumberStyles.Float, Inv, out var v) ? v : double.NaN;
    }

    private void SalvarTaxas()
    {
        if (!_isAdmin) return;
        var ok = new Dictionary<int, double?>();
        foreach (var a in _taxasAnos)
        {
            var v = LerTaxa(_taxasTxt.GetValueOrDefault(a, ""));
            if (v is { } x && (double.IsNaN(x) || x <= 0 || x >= 100)) { _taxasErro = $"Taxa inválida em {a}. Use um número entre 0 e 100 (ex.: 5,2000)."; return; }
            ok[a] = v;
        }
        foreach (var (a, v) in ok)
        {
            if (v is { } t) { if (Cambio.Taxa(a) != t) Cambio.Salvar(a, t, _user); }
            else if (Cambio.Taxa(a) is not null) Cambio.Remover(a);
        }
        _taxasErro = ""; _taxasAberto = false;
    }

    /// <summary>Por ano: taxa aplicada, propostas, US$ e R$ — a conversão rastreável.</summary>
    private List<(string Ano, double? Taxa, int Propostas, double Usd, double Brl, int SemTaxa)> RastroCambio() =>
        _f.GroupBy(AnoTxt).OrderBy(g => g.Key)
          .Select(g => (Ano: g.Key,
                        Taxa: g.Select(i => i.Taxa).FirstOrDefault(t => t is not null),
                        Propostas: g.Count(),
                        Usd: g.Sum(i => i.P.ValorUsd ?? 0),
                        Brl: g.Sum(i => i.Brl ?? 0),
                        SemTaxa: g.Count(i => i.P.ValorUsd is not null && i.Brl is null))).ToList();
}
