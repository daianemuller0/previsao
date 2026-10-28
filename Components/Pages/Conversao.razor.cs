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
public partial class Conversao : IAsyncDisposable
{
    // ---- cultura, cores e nomes ------------------------------------------------
    private static readonly System.Globalization.CultureInfo _pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    // Um conceito, uma cor, em todas as visualizações.
    private const string CorG = "#1f9254", CorP = "#d98324", CorA = "#007A9E", CorN = "#93a1b5";
    // Taxa de conversão tem cor própria (azul-marinho); #007A9E fica exclusivo de Abertas.
    private const string CorTx = "#12355B";
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
        ClasseProposta.Aberta => "Abertas: status Open — ainda sem resultado.",
        _ => "Não classificadas: status fora da lista combinada, aguardando validação. Ficam fora de ganhas, perdidas e abertas.",
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
    // Largura mínima de cada chip (rótulo + "…" + ✕) e da faixa de chips: abaixo disso a faixa
    // desce para a linha de baixo, em vez de os chips se sobreporem aos filtros.
    private static string ChipTxt(HashSet<string> set) => string.Join(", ", set.Take(2)) + (set.Count > 2 ? $" +{set.Count - 2}" : "");
    private static string ChipRot(string rot) => rot == "BU Intercompany" ? "BU IC" : rot;
    private static int ChipMin(string rot, HashSet<string> set) => 58 + 8 * ChipRot(rot).Length + Math.Min(ChipTxt(set).Length, 4) * 8;
    // Chip curto não encolhe (nada a cortar); o longo pode reduzir até rótulo + 4 letras e "…".
    private static string ChipStyle(string rot, HashSet<string> set) =>
        ChipTxt(set).Length <= 4 ? "flex:none" : $"min-width:{ChipMin(rot, set)}px";
    private int ChipsMin() => ConjuntosAtivos().Sum(x => ChipMin(x.Item1, x.Item2) + 6) + (_seg is { } sg ? 64 + 8 * ("Segmento".Length + NomeEn(sg).Length) : 0) + 112;
    private void Limpar() { _anos.Clear(); _meses.Clear(); _vend.Clear(); _bus.Clear(); _buIcs.Clear(); _seg = null; _busca = ""; _classes.Clear(); _pag = 0; _limpezas++; }
    private int _limpezas;
    private void AlternarSeg(ClasseProposta c) => _seg = _seg == c ? null : c;

    private static void Alternar(HashSet<string> set, string v) { if (!set.Remove(v)) set.Add(v); }

    // ---- modelo calculado --------------------------------------------------------
    private sealed record Item(PropostaConv P, int? Ano, int? Mes, double? Taxa, double? Brl);

    private sealed class Resumo
    {
        public int Total, G, P, A, NC, SemTaxa, SemValor, SemData;
        public double VG, VP, VA, VNC, UsdSemTaxa, UsdSemData;
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
    private string _prevRotulo = "";                 // rótulo do período comparável: "2025" ou "jan–out/2025"
    private (int, int) _chave = (-1, -1);

    private void Recalcular()
    {
        var chave = (Conv.Version, Cambio.Version);
        if (chave != _chave) { Reconstruir(); _chave = chave; }
        _f = Aplicar().ToList();
        _fx = _seg is { } s ? _f.Where(i => i.P.Classe == s).ToList() : _f;
        _r = Somar(_f);
        _rx = ReferenceEquals(_fx, _f) ? _r : Somar(_fx);
        _prev = null; _prevRotulo = "";
        // Período comparável: só com UM ano escolhido — mesmos filtros e meses, ano anterior.
        // Sem filtro de mês, compara o mesmo período do ano (YTD): jan até o último mês com dados do ano
        // na BASE INTEIRA (o corte de calendário não pode depender de vendedor/BU filtrados).
        if (_anos.Count == 1 && int.TryParse(_anos.First(), out var a))
        {
            var mesMax = _meses.Count > 0 ? 12 : _itens.Where(i => i.Ano == a && i.Mes is not null).Select(i => i.Mes!.Value).DefaultIfEmpty(12).Max();
            var p = Somar(Aplicar("ano").Where(i => i.Ano == a - 1 && i.Mes is { } m && m <= mesMax));
            if (p.Total > 0) _prev = p;
            string MesAbrev(int m) => new DateTime(2000, m, 1).ToString("MMM", _pt).TrimEnd('.');
            _prevRotulo = mesMax >= 12 ? (a - 1).ToString() : mesMax == 1 ? $"jan/{a - 1}" : $"jan\u2060–\u2060{MesAbrev(mesMax)}/{a - 1}"; // word joiner: o rótulo não quebra no traço
        }
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
                var usd = i.P.ValorUsd.Value;
                // sem data de envio não há ano, logo não há taxa: são problemas distintos
                if (i.Ano is null) { r.SemData++; r.UsdSemData += usd; }
                else if (i.Brl is null) { r.SemTaxa++; r.UsdSemTaxa += usd; }
                else r.Conv[k]++;
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
        _f.Where(i => i.Ano is not null && i.P.ValorUsd is not null && i.Brl is null).Select(AnoTxt).Distinct().OrderBy(x => x);

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
    /// <summary>Rótulo de eixo: marcas redondas sem a casa decimal vazia ("60 mi", não "60,0 mi").</summary>
    private static string MonEixo(double v) { var t = Mon(v); return t.EndsWith(",0 mi") ? t[..^5] + " mi" : t.EndsWith(",00 bi") ? t[..^6] + " bi" : t; }
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
        : Fmt.Brl(v) + (conv < comUsd ? $" · parcial: {comUsd - conv} proposta(s) sem taxa de câmbio ou sem data de envio ficaram de fora." : "");

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
        anterior is > 0 && _prev is not null ? $"{(atual - anterior.Value) / anterior.Value * 100:+0;-0;0}% vs {_prevRotulo}" : null;

    /// <summary>Cor da variação: pos/neg conforme o sentido desejado do indicador; null = neutro (sem juízo de valor).</summary>
    private static string DeltaCls(string? d, bool? maior) =>
        d is null || maior is null || d.StartsWith("+0") || d.StartsWith("0") ? "neu" : (d.StartsWith("+") == maior.Value ? "pos" : "neg");
    /// <summary>Seta ▲/▼ à frente da variação (o texto continua começando pelo sinal, que DeltaCls lê).</summary>
    private static string Seta(string d) => d.StartsWith("+0") || d.StartsWith("0") ? d : (d.StartsWith("+") ? "▲ " : d.StartsWith("-") ? "▼ " : "") + d;

    private (string Texto, string Css) DeltaWin()
    {
        if (_anos.Count != 1 || !int.TryParse(_anos.First(), out var ano))
            return ("", "neu");
        if (_prev is null) return ($"Sem dados comparáveis em {_prevRotulo}", "neu");
        if (_r.TaxaQtd is not { } cur || _prev.TaxaQtd is not { } ant) return ($"Sem propostas decididas para comparar com {_prevRotulo}", "neu");
        var d = cur - ant;
        if (Math.Abs(d) < 0.05) return ($"Estável vs {_prevRotulo} (0,0 p.p.)", "neu");
        return ($"{(d > 0 ? "▲" : "▼")} {Pp(d)} vs {_prevRotulo} ({P1(ant)})", d > 0 ? "pos" : "neg");
    }

    private string PeriodoTxt()
    {
        string anos;
        if (_anos.Count > 0)
            anos = string.Join(", ", _anos.OrderBy(x => x));
        else
        {
            // sem filtro de ano: mostra o intervalo real de envio do recorte
            var ds = _f.Where(i => i.P.Data is not null).Select(i => i.P.Data!.Value).ToList();
            anos = ds.Count == 0 ? "Todos os anos" : $"{ds.Min().ToString("MMM/yyyy", _pt)} a {ds.Max().ToString("MMM/yyyy", _pt)}";
        }
        // meses em ordem de calendário, não na ordem em que foram marcados
        var ms = Enumerable.Range(1, 12).Select(Fmt.MonthLong).Where(_meses.Contains).ToList();
        var meses = ms.Count == 0 ? "" : " · " + string.Join(", ", ms.Take(3)) + (ms.Count > 3 ? $" +{ms.Count - 3}" : "");
        return anos + meses;
    }

    private string NotasFiltros()
    {
        var l = ConjuntosAtivos().Select(x => $"{x.Item1}: {string.Join(", ", x.Item2.Take(3))}{(x.Item2.Count > 3 ? "…" : "")}").ToList();
        return l.Count == 0 ? "Sem filtros adicionais" : string.Join(" · ", l);
    }

    private const int AmostraMinima = 20;   // decididas para comparar taxas sem ruído

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
    private string SvgWinRate(IEnumerable<Item> src, int offset, bool compact, bool comparar = true, double largura = 1000)
    {
        var (meses, por) = Janela(src, offset);
        if (meses.Count == 0) return Vazio("Nenhuma proposta com data de envio no recorte.");
        // O comparativo "mesmo mês do ano anterior" usa o recorte global; num recorte
        // individual (vendedor) ele compararia coisas diferentes e fica desligado.
        var hist = comparar ? SerieBase() : new Dictionary<DateTime, Resumo>();

        // largura menor (drawer, PDF) = viewBox mais estreito: o SVG escala menos e as fontes continuam legíveis
        double W = largura, H = compact ? 256 : 320, L = 46, Rr = 16, T = 14, B = 26, strip = 46, gap = compact ? 18 : 12;
        var fs = compact ? 11 : 10.5;
        var plotH = H - T - B - strip - gap;
        var passo = (W - L - Rr) / meses.Count;
        var soPares = passo < 50;   // pouco espaço por mês: rotula só os meses pares
        double X(int i) => L + passo * (i + 0.5);
        double Y(double v) => T + plotH * (1 - v / 100);
        var maxD = Math.Max(meses.Max(m => por.TryGetValue(m, out var rr) ? rr.Decididas : 0), 1);
        var baseStrip = T + plotH + gap + strip;

        var sb = new StringBuilder("<div class='chart-body'>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='taxa de conversão mensal'>");
        foreach (var v in new[] { 0, 25, 50, 75, 100 })
        {
            sb.Append($"<line x1='{F(L)}' y1='{F(Y(v))}' x2='{F(W - Rr)}' y2='{F(Y(v))}' stroke='#e8edf4'/>");
            sb.Append($"<text x='{F(L - 8)}' y='{F(Y(v) + 3)}' text-anchor='end' font-size='{F(fs)}' fill='#6b7a90'>{v}%</text>");
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
        if (atual != "") sb.Append($"<path d='{atual}' fill='none' stroke='{CorTx}' stroke-width='2.6' stroke-linejoin='round' stroke-linecap='round'/>");

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
                sb.Append($"<circle cx='{F(x)}' cy='{F(Y(r.TaxaQtd!.Value))}' r='4.5' fill='{(imaturo ? "#FFFFFF" : CorTx)}' stroke='{CorTx}' stroke-width='2'/>");
            }
            hist.TryGetValue(meses[i].AddYears(-1), out var ra);
            var tip = $"{meses[i].ToString("MMMM/yyyy", _pt)}\n" +
                      (r is null ? "Sem propostas enviadas" :
                       $"taxa de conversão: {(r.Decididas > 0 ? P1(r.TaxaQtd!.Value) : "não calculável")} ({N0(r.G)} ganhas de {N0(r.Decididas)} decididas)\n" +
                       $"Perdidas: {N0(r.P)} · Abertas: {N0(r.A)} ({PctDe(r.A, r.Total)} do mês)" +
                       (r.NC > 0 ? $" · Não classificadas: {N0(r.NC)}" : "")) +
                      (ra is not null && ra.Decididas > 0 ? $"\nMesmo mês de {meses[i].Year - 1}: {P1(ra.TaxaQtd!.Value)} ({N0(ra.Decididas)} decididas)" : "");
            sb.Append($"<rect x='{F(L + passo * i)}' y='{F(T)}' width='{F(passo)}' height='{F(H - T - B + 6)}' fill='transparent'><title>{EscTip(tip)}</title></rect>");
            if (!soPares || meses[i].Month % 2 == 0)
                sb.Append($"<text x='{F(x)}' y='{F(H - 8)}' text-anchor='middle' font-size='{F(fs)}' fill='#3b4a61'>{Esc(meses[i].ToString("MMM/yy", _pt))}</text>");
        }
        sb.Append($"<text x='{F(L - 8)}' y='{F(baseStrip - strip + 6)}' text-anchor='end' font-size='{F(compact ? 10.5 : 9.5)}' fill='#6b7a90'>{maxD}</text>");
        sb.Append($"<text x='{F(L - 8)}' y='{F(baseStrip)}' text-anchor='end' font-size='{F(compact ? 10.5 : 9.5)}' fill='#6b7a90'>0</text>");
        sb.Append("</svg>");
        sb.Append("<div class='cvx-leg'>");
        sb.Append($"<span><i class='cvx-ln' style='border-top-color:{CorTx}'></i>Taxa de conversão do mês</span>");
        if (comparar) sb.Append("<span><i class='cvx-ln dash'></i>Mesmo mês do ano anterior</span>");
        sb.Append($"<span><i class='cvx-pt' style='border-color:{CorTx}'></i>Mais de 30% das propostas do mês ainda abertas (a taxa pode mudar)</span>");
        sb.Append("<span><i class='cvx-bx'></i>Propostas decididas (escala própria, embaixo)</span>");
        sb.Append("</div></div>");
        return sb.ToString();
    }

    /// <summary>Colunas empilhadas por mês: quantidade ou valor (R$), sempre partindo do zero.</summary>
    private int? _anoBarras;
    private List<int> AnosBarras() => _f.Where(i => i.Ano is not null).Select(i => i.Ano!.Value).Distinct().OrderBy(a => a).ToList();
    private int AnoBarrasAtual()
    {
        var anos = AnosBarras();
        if (anos.Count == 0) return DateTime.Today.Year;
        return _anoBarras is { } y && anos.Contains(y) ? y : anos[^1];
    }

    /// <summary>Passo "redondo" do eixo (1, 2, 2,5 ou 5 × 10ⁿ) imediatamente acima de v.</summary>
    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        var p = Math.Pow(10, Math.Floor(Math.Log10(v))); var f = v / p;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p;
    }

    /// <summary>Barras empilhadas de janeiro a dezembro do ano escolhido (ordem cronológica do calendário).</summary>
    private string SvgEmpilhado(IEnumerable<Item> src, bool qtd)
    {
        var ano = AnoBarrasAtual();
        var meses = Enumerable.Range(1, 12).Select(m => new DateTime(ano, m, 1)).ToList();
        var por = src.Where(i => i.Ano == ano && i.Mes is not null).GroupBy(i => new DateTime(ano, i.Mes!.Value, 1)).ToDictionary(g => g.Key, g => Somar(g));
        if (por.Count == 0) return Vazio("Nenhuma proposta com data de envio em " + ano + ".");
        // valor nunca vira zero silencioso: sem taxa do ano, o gráfico diz isso em vez de barras vazias
        if (!qtd && por.Values.All(r => r.TotConv == 0))
            return Vazio(por.Values.Any(r => r.TotComUsd > 0) ? $"Valores de {ano} não convertidos: falta a taxa USD/BRL do ano." : $"Nenhuma proposta com valor em US$ em {ano}.");
        var temParcial = false;

        double Val(Resumo r, int c) => qtd
            ? (c == 0 ? r.G : c == 1 ? r.P : c == 2 ? r.A : r.NC)
            : (c == 0 ? r.VG : c == 1 ? r.VP : c == 2 ? r.VA : r.VNC);
        var temNc = por.Values.Any(r => r.NC > 0);
        var cats = temNc ? 4 : 3;
        double maxY = 1;
        foreach (var r in por.Values) maxY = Math.Max(maxY, Enumerable.Range(0, cats).Sum(c => Val(r, c)));
        // 4 faixas de passo redondo, com folga de 12% para o total escrito em cima da coluna
        // 3 ou 4 faixas: o eixo para na primeira marca redonda acima do maior total (sem metade do gráfico vazia)
        var step = Nice(maxY * 1.12 / 4); var nTicks = Math.Max(2, (int)Math.Ceiling(maxY * 1.12 / step)); maxY = step * nTicks;

        // T deixa uma faixa acima da marca mais alta para a unidade "R$" não encostar no rótulo do eixo
        const double W = 1200, H = 372, L = 84, Rr = 18, T = 44, B = 36;
        var passo = (W - L - Rr) / meses.Count; var larg = Math.Min(passo * 0.6, 56);
        double Y(double v) => T + (H - T - B) * (1 - v / maxY);
        string Rot(double v) => qtd ? N0((int)Math.Round(v)) : MonEixo(v);

        var sb = new StringBuilder("<div class='chart-body'>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='{(qtd ? "Volume" : "Valor")} mensal'>");
        if (!qtd) sb.Append($"<text x='{F(L - 8)}' y='{F(T - 16)}' text-anchor='end' font-size='12' font-weight='700' fill='#6b7a90'>R$</text>");
        for (var k = 0; k <= nTicks; k++)
        {
            var v = step * k;
            sb.Append($"<line x1='{F(L)}' y1='{F(Y(v))}' x2='{F(W - Rr)}' y2='{F(Y(v))}' stroke='#e8edf4'/>");
            sb.Append($"<text x='{F(L - 12)}' y='{F(Y(v) + 4)}' text-anchor='end' font-size='12.5' fill='#8a97ab'>{Esc(Rot(v))}</text>");
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
                var parcial = !qtd && r.TotConv > 0 && r.TotConv < r.TotComUsd;
                if (parcial) temParcial = true;
                if (acum > 0)
                    sb.Append($"<text x='{F(x + larg / 2)}' y='{F(Y(acum) - 6)}' text-anchor='middle' font-size='12.5' font-weight='700' fill='#3b4a61'>{Esc(qtd ? N0((int)acum) : Mon(acum) + (parcial ? "*" : ""))}</text>");
                else if (!qtd && r.TotComUsd > 0 && r.TotConv == 0)
                    sb.Append($"<text x='{F(x + larg / 2)}' y='{F(Y(0) - 6)}' text-anchor='middle' font-size='11' fill='#c0392b'>n/c<title>Não convertido: falta a taxa de câmbio</title></text>");
            }
            sb.Append($"<text x='{F(x + larg / 2)}' y='{F(H - 12)}' text-anchor='middle' font-size='12.5' font-weight='600' fill='#8a97ab' letter-spacing='.06em'>{Esc(meses[i].ToString("MMM", _pt).TrimEnd('.').ToUpper(_pt))}</text>");
        }
        sb.Append("</svg><div class='cvx-leg'>");
        for (var c = 0; c < cats; c++) sb.Append($"<span><i class='cvx-dot' style='background:{Cor(Classes[c])}'></i>{NomeEn(Classes[c])}</span>");
        sb.Append("</div>");
        if (temParcial) sb.Append("<p class='cvx-note'>* mês com propostas sem taxa de câmbio (valor parcial)</p>");
        sb.Append("</div>");
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
        // marcas redondas; o eixo para na primeira marca acima do maior valor (com folga para o rótulo do ano atual)
        var step = Nice(maxV / 4);
        var nTicks = met == "winrate" ? 4 : Math.Max(2, (int)Math.Ceiling(maxV * 1.08 / step));
        var maxY = met == "winrate" ? 100 : step * nTicks;

        // Paleta de anos em tons de azul/cinza: sem verde, laranja, vermelho ou roxo, que já significam status.
        // Ano atual em azul-marinho sólido; anteriores mais claros e tracejados. Nenhum ano é cortado e as cores
        // não são recicladas (o 7º ano não pode herdar o azul do atual): além do 6º, todos ficam no último cinza e a legenda avisa.
        string[] cores = { "#12355B", "#4F7AA8", "#8DA2BC", "#B3C0D1", "#6E7F96", "#C9D3DE" };
        string CorAno(int k) => cores[Math.Min(k, cores.Length - 1)];
        string? Traco(int k) => k == 0 ? null : k == 1 ? "6 4" : "2 4";
        // mês corrente real (calendário), não o último mês com dado
        var hoje = DateTime.Today; var mesAtual = anos[0] == hoje.Year ? hoje.Month : 0;
        double W = 1200, H = 400, L = 70, Rr = 30, T = 44, B = 40;
        var passo = (W - L - Rr) / 12;
        double X(int m) => L + passo * (m - 0.5);
        double Y(double v) => T + (H - T - B) * (1 - v / maxY);
        string Lbl(double v) => met == "winrate" ? Math.Round(v).ToString(Inv) + "%" : Rot(v);

        // Linhas retas ponto a ponto (padrão das polylines da Visão Executiva): sem curvas que inventem picos.
        string Suave(List<(double X, double Y)> p) => "M " + string.Join(" L ", p.Select(q => F(q.X) + "," + F(q.Y)));

        // Ponto frágil: mês em andamento ou, na taxa, poucas decididas / mais de 30% ainda abertas.
        bool Fraco(int a, int m)
        {
            if (a == hoje.Year && m == hoje.Month) return true;
            if (met != "winrate") return false;
            por.TryGetValue((a, m), out var rr);
            return rr is not null && (rr.Decididas < 5 || (rr.Total > 0 && (double)rr.A / rr.Total > .30));
        }
        var temFraco = Enumerable.Range(1, 12).Any(m => Val(anos[0], m) is not null && Fraco(anos[0], m));

        var sb = new StringBuilder("<div class='chart-body cvx-anual'><div class='cvx-leg top'>");
        for (var k = 0; k < anos.Count; k++)
            sb.Append($"<span><i class='cvx-ln{(k == 1 ? " dash" : k >= 2 ? " dash dot" : "")}' style='border-top-color:{CorAno(k)}'></i>{anos[k]}</span>");
        if (anos.Count > cores.Length)
            sb.Append($"<span class='cvx-leg-aviso'>{anos.Count} anos no recorte: de {anos[^1]} a {anos[cores.Length - 1]} na mesma cor cinza · filtre por Ano para comparar menos anos</span>");
        if (temFraco)
            sb.Append($"<span><i class='cvx-pt' style='border-color:{cores[0]}'></i>{(met == "winrate" ? "Mês em andamento, poucas decididas ou mais de 30% abertas: o valor pode mudar" : "Mês em andamento: o valor pode mudar")}</span>");
        sb.Append("</div>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='evolução mensal por ano' preserveAspectRatio='xMidYMid meet'>");
        sb.Append($"<defs><linearGradient id='cvArea' x1='0' y1='0' x2='0' y2='1'><stop offset='0%' stop-color='{cores[0]}' stop-opacity='.18'/><stop offset='100%' stop-color='{cores[0]}' stop-opacity='0'/></linearGradient></defs>");
        // título do eixo: a unidade fica explícita (taxa, quantidade ou R$)
        sb.Append($"<text x='{F(L)}' y='{F(T - 26)}' font-size='12' font-weight='700' fill='#6b7a90'>{(met == "winrate" ? "Taxa de conversão (%)" : met == "volume" ? "Propostas enviadas" : "Valor (R$)")}</text>");
        for (var g = 0; g <= nTicks; g++)
        {
            var v = met == "winrate" ? 25.0 * g : step * g;
            sb.Append($"<line x1='{F(L)}' y1='{F(Y(v))}' x2='{F(W - Rr)}' y2='{F(Y(v))}' stroke='{(g == 0 ? "#d3dcea" : "#edf1f6")}' stroke-width='{F(g == 0 ? 1.2 : 1)}'/>");
            sb.Append($"<text x='{F(L - 14)}' y='{F(Y(v) + 4)}' text-anchor='end' font-size='12.5' fill='#8a97ab'>{Esc(met == "valor" ? MonEixo(v) : Lbl(v))}</text>");
        }
        // faixa do mês atual
        if (mesAtual > 0)
            sb.Append($"<rect x='{F(L + passo * (mesAtual - 1))}' y='{F(T)}' width='{F(passo)}' height='{F(H - T - B)}' fill='{cores[0]}' opacity='.045' rx='6'/>");
        for (var m = 1; m <= 12; m++)
            sb.Append($"<text x='{F(X(m))}' y='{F(H - 14)}' text-anchor='middle' font-size='12.5' font-weight='{(m == mesAtual ? 700 : 600)}' fill='{(m == mesAtual ? cores[0] : "#8a97ab")}' letter-spacing='.06em'>{Esc(new DateTime(2000, m, 1).ToString("MMM", _pt).TrimEnd('.').ToUpper(_pt))}</text>");

        // anos antigos primeiro, para o mais recente ficar por cima
        for (var k = anos.Count - 1; k >= 0; k--)
        {
            var a = anos[k]; var cor = CorAno(k); var atual = k == 0;
            var traco = Traco(k) is { } tr ? $" stroke-dasharray='{tr}'" : "";
            var segs = new List<List<(double X, double Y)>>(); var seg = new List<(double X, double Y)>();
            for (var m = 1; m <= 12; m++)
            {
                var v = Val(a, m);
                if (v is null) { if (seg.Count > 0) { segs.Add(seg); seg = new(); } continue; }
                seg.Add((X(m), Y(v.Value)));
            }
            if (seg.Count > 0) segs.Add(seg);
            foreach (var s in segs)
            {
                var caminho = Suave(s);
                if (atual && met != "winrate" && s.Count > 1)
                    sb.Append($"<path d='{caminho} L {F(s[^1].X)},{F(Y(0))} L {F(s[0].X)},{F(Y(0))} Z' fill='url(#cvArea)' stroke='none'/>");
                if (s.Count > 1)
                    sb.Append($"<path d='{caminho}' fill='none' stroke='{cor}' stroke-width='{F(atual ? 4 : 2.6)}'{traco} stroke-linejoin='round' stroke-linecap='round' opacity='{F(atual ? 1 : 0.9)}'/>");
            }
            for (var m = 1; m <= 12; m++)
            {
                var v = Val(a, m); if (v is null) continue;
                var tip = EscTip($"{a} · {new DateTime(2000, m, 1).ToString("MMMM", _pt)}\n{Txt(a, m)}");
                if (atual)
                {
                    // ponto cheio = valor consolidado; vazado e tracejado = pode mudar
                    var fraco = Fraco(a, m);
                    sb.Append(fraco
                        ? $"<circle cx='{F(X(m))}' cy='{F(Y(v.Value))}' r='6' fill='#fff' stroke='{cor}' stroke-width='2.5' stroke-dasharray='3 2'><title>{tip}</title></circle>"
                        : $"<circle cx='{F(X(m))}' cy='{F(Y(v.Value))}' r='6' fill='{cor}' stroke='#fff' stroke-width='2.5'><title>{tip}</title></circle>");
                    // rótulo sem pílula: halo branco (paint-order) separa o texto das linhas; desce para baixo
                    // do ponto quando outro ano passa logo acima, para não encavalar
                    var lab = Lbl(v.Value);
                    var yv = Y(v.Value);
                    var acima = anos.Skip(1).Select(o => Val(o, m)).Where(x => x is not null).Any(x => { var oy = Y(x!.Value); return oy < yv && yv - oy < 28; });
                    var ly = acima ? yv + 20 : yv - 12;
                    sb.Append($"<text x='{F(X(m))}' y='{F(ly)}' text-anchor='middle' font-size='12' font-weight='700' fill='{cor}' fill-opacity='{(fraco ? ".72" : "1")}' paint-order='stroke' stroke='#fff' stroke-width='4' stroke-linejoin='round'>{Esc(lab)}</text>");
                }
                else
                    sb.Append($"<circle cx='{F(X(m))}' cy='{F(Y(v.Value))}' r='4' fill='{cor}' stroke='#fff' stroke-width='1.5'><title>{tip}</title></circle>");
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
            sb.Append($"<span class='cvx-seg {ClasseCss(c)}' style='width:{F(pct)}%;background:{Cor(c)}' title='{EscTip($"{NomeEn(c)}: {N0(n)} ({PctDe(n, r.Total)})\n{DefClasse(c)}")}'>{(pct >= 8 ? Esc(P1(pct)) : "")}</span>");
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

    private List<(string Nome, int Qtd, double Valor, int ComUsd, int Conv)> LinhasPareto(bool qtd, bool comSegmento)
    {
        // Na página segue o segmento da barra de composição (_fx); no relatório, o recorte global (_f).
        var perdidas = (comSegmento ? _fx : _f).Where(i => i.P.Classe == ClasseProposta.Perdida).ToList();
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
    private string HtmlPareto(bool qtd, bool comSegmento = true)
    {
        var linhas = LinhasPareto(qtd, comSegmento);
        var totQ = linhas.Sum(x => x.Qtd); var totV = linhas.Sum(x => x.Valor);
        if (totQ == 0) return Vazio("Nenhuma proposta perdida no recorte.");
        var max = Math.Max(linhas.Max(x => qtd ? x.Qtd : x.Valor), 0.0000001);
        var total = qtd ? totQ : totV;
        var sb = new StringBuilder("<div class='cvx-pareto'>");
        sb.Append("<div class='cvx-prow head'><span>Status no CRM</span><span></span><span class='r'>" + (qtd ? "Propostas" : "Valor") + "</span><span class='r'>Participação</span><span class='r'>Acumulado</span></div>");
        double acum = 0;
        foreach (var l in linhas.Where(x => x.Qtd > 0))
        {
            var v = qtd ? l.Qtd : l.Valor; acum += v;
            var valorTxt = qtd ? N0(l.Qtd) : Estado(l.ComUsd, l.Conv, l.Valor);
            var tip = $"{l.Nome}\n{N0(l.Qtd)} proposta(s) · {EstadoTip(l.ComUsd, l.Conv, l.Valor)}";
            sb.Append($"<div class='cvx-prow' title='{EscTip(tip)}'><span class='nm'>{Esc(l.Nome)}</span>");
            sb.Append($"<span class='bar'><i style='width:{F(v / max * 100)}%;background:{CorP}'></i></span>");
            sb.Append($"<span class='r'><b>{Esc(valorTxt)}</b></span><span class='r'>{Esc(PctDe(v, total))}</span><span class='r muted'>{Esc(PctDe(acum, total))}</span></div>");
        }
        sb.Append("</div>");
        var zeros = linhas.Where(x => x.Qtd == 0).Select(x => x.Nome).ToList();
        if (zeros.Count > 0) sb.Append($"<p class='cvx-note'>Sem ocorrências no recorte: {Esc(string.Join(", ", zeros))}</p>");
        return sb.ToString();
    }

    private string TextoInsightPerda(bool comSegmento = true)
    {
        var linhas = LinhasPareto(true, comSegmento);
        var tot = linhas.Sum(x => x.Qtd);
        if (tot == 0) return "Sem propostas perdidas no recorte atual.";
        var top = linhas.First();
        var totV = linhas.Sum(x => x.Valor);
        var s = $"{top.Nome} representa {PctDe(top.Qtd, tot)} das propostas classificadas como perdidas ({N0(top.Qtd)} de {N0(tot)}).";
        var topV = LinhasPareto(false, comSegmento).First();
        if (totV > 0 && topV.Nome != top.Nome) s += $" Em valor, o maior impacto é de {topV.Nome} ({PctDe(topV.Valor, totV)} do valor perdido convertido).";
        else if (totV > 0) s += $" Em valor, também lidera, com {PctDe(topV.Valor, totV)} do valor perdido convertido.";
        return s;
    }

    private string HtmlVendTop(int n, int minDec)
    {
        var l = Grupos(_f, i => i.P.Vendedor).Where(x => x.R.Decididas >= minDec && x.R.TaxaQtd is not null)
            .OrderByDescending(x => x.R.TaxaQtd).ThenByDescending(x => x.R.Decididas).Take(n).ToList();
        if (l.Count == 0) return Vazio($"Nenhum vendedor com {minDec}+ propostas decididas no recorte.");
        var sb = new StringBuilder("<table class='cvx-mini-tbl'><thead><tr><th>#</th><th>Vendedor</th><th class='r'>Taxa de conversão</th><th class='r'>Ganhas</th><th class='r'>Perdidas</th><th class='r'>Abertas</th><th class='r'>Decididas</th><th class='r'>Valor ganho</th></tr></thead><tbody>");
        for (var i = 0; i < l.Count; i++)
        {
            var (nome, r) = l[i];
            sb.Append($"<tr><td>{i + 1}</td><td><b>{Esc(nome)}</b></td><td class='r'><b>{Esc(TxQtdGlobal(r))}</b></td><td class='r'>{N0(r.G)}</td><td class='r'>{N0(r.P)}</td><td class='r'>{N0(r.A)}</td><td class='r'>{N0(r.Decididas)}</td><td class='r' title='{EscTip(DinTip(r, ClasseProposta.Ganha))}'>{Esc(Din(r, ClasseProposta.Ganha))}</td></tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    /// <summary>Ranking de BU do relatório: só unidades com amostra mínima; a marca vertical é a taxa do recorte.</summary>
    private string HtmlBuBars(List<(string Nome, Resumo R)> grupos, int max, int minDec)
    {
        var l = grupos.Where(x => x.R.Decididas >= minDec && x.R.TaxaQtd is not null)
            .OrderByDescending(x => x.R.TaxaQtd).ThenByDescending(x => x.R.Decididas).Take(max).ToList();
        var fora = grupos.Count - l.Count;
        if (l.Count == 0) return Vazio($"Nenhuma unidade com {minDec}+ propostas decididas.");
        var media = F(_r.TaxaQtd ?? 0);
        var sb = new StringBuilder("<div class='cvx-bubars'>");
        foreach (var (nome, r) in l)
            sb.Append($"<div class='cvx-bb' title='{EscTip($"{nome}\n{N0(r.G)} ganhas de {N0(r.Decididas)} decididas")}'><span class='nm'>{Esc(nome)}</span>"
                + $"<span class='bar' style='position:relative;overflow:visible'><i style='width:{F(r.TaxaQtd!.Value)}%;background:{CorTx}'></i>"
                + $"<em style='position:absolute;left:{media}%;top:-3px;bottom:-3px;width:2px;background:#3b4a61'></em></span>"
                + $"<b>{Esc(P1(r.TaxaQtd.Value))}</b><small>{N0(r.Decididas)} decididas</small></div>");
        sb.Append("</div>");
        if (fora > 0) sb.Append($"<p class='rpt-small'>{fora} unidade(s) com menos de {minDec} decididas ou além das {max} primeiras não aparecem.</p>");
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
            sb.Append($"<span class='cvx-seg {ClasseCss(c)}' style='width:{F(pct)}%;background:{Cor(c)}' title='{EscTip($"{NomeEn(c)}: {DinTip(r, c)}")}'>{(pct >= 9 ? Esc(P1(pct)) : "")}</span>");
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
        // participação sobre o total de TODAS as unidades convertidas, não só do top 10
        var todos = grupos.Where(x => x.R.TotConv > 0).ToList();
        var soma = Math.Max(todos.Sum(x => x.R.VTotal), 1e-7);
        var l = todos.OrderByDescending(x => x.R.VTotal).Take(10).ToList();
        var semConv = grupos.Count(x => x.R.TotComUsd > 0 && x.R.TotConv == 0);
        if (l.Count == 0) return Vazio(semConv > 0 ? "Valores por unidade não convertidos: falta a taxa USD/BRL do ano." : "Sem valores convertidos por unidade no recorte.");
        var max = Math.Max(l.Max(x => x.R.VTotal), 0.0000001);
        var sb = new StringBuilder("<div class='rank cvx-rk'>");
        sb.Append("<div class='rank-head'><span></span><span>Unidade</span><span>Ganho · perdido · aberto</span><span class='r'>Valor total</span><span class='r'>Part.</span></div>");
        var pos = 0;
        foreach (var (nome, r) in l)
        {
            pos++;
            var tip = $"{nome}\nGanho: {Din(r, ClasseProposta.Ganha)}\nPerdido: {Din(r, ClasseProposta.Perdida)}\nAberto: {Din(r, ClasseProposta.Aberta)}";
            sb.Append($"<div class='rank-row{(pos == 1 ? " top1" : pos == 2 ? " top2" : pos == 3 ? " top3" : "")}' title='{EscTip(tip)}'>");
            sb.Append($"<span class='rank-num'>{pos}</span><span class='rank-name'>{Esc(nome)}</span>");
            sb.Append($"<span class='rank-barwrap'><span class='rank-track'><span class='cvx-stk' style='width:{F(r.VTotal / max * 100)}%'>");
            foreach (var cl in Classes) if (r.Val(cl) > 0) sb.Append($"<i style='flex:{F(r.Val(cl))};background:{Cor(cl)}'></i>");
            sb.Append($"</span></span></span><span class='rank-val' title='{EscTip(DinTotalTip(r))}'>{Esc(DinTotal(r))}</span><span class='rank-share'>{Esc(PctDe(r.VTotal, soma))}</span></div>");
        }
        sb.Append("</div><div class='cvx-leg'>");
        foreach (var cl in Classes) if (cl != ClasseProposta.NaoClassificada || l.Any(x => x.R.VNC > 0)) sb.Append($"<span><i class='cvx-dot' style='background:{Cor(cl)}'></i>{NomeEn(cl)}</span>");
        sb.Append("</div>");
        if (semConv > 0) sb.Append($"<p class='cvx-note'>{semConv} unidade(s) com valor em US$ sem taxa de câmbio não aparecem.</p>");
        if (todos.Count > 10) sb.Append($"<p class='cvx-note'>Top 10 de {todos.Count} unidades; participação sobre o total do recorte.</p>");
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

    // A tabela ignora o próprio filtro (BU não filtra a tabela de BU): a seleção aparece destacada,
    // as demais unidades ficam esmaecidas e continuam clicáveis.
    private List<(string Nome, Resumo R)> UnidadesOrdenadas(Func<Item, string> sel, string exceto)
    {
        var src = Aplicar(exceto);
        if (_seg is { } s) src = src.Where(i => i.P.Classe == s);
        return Grupos(src, sel).OrderByDescending(x => x.R.Decididas > 0 && _taxaOk ? x.R.TaxaQtd : -1)
                        .ThenByDescending(x => x.R.Decididas).ThenByDescending(x => x.R.VTotal).ToList();
    }

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
            new("brl", "Valor convertido (R$)", "right", i => i.Brl is { } b ? b.ToString("#,##0.00", _pt) : (i.P.ValorUsd is null ? "—" : "Não convertido"), i => i.Brl),
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
        // O workbook é montado fora do circuito do Blazor: bases grandes não travam a tela.
        var bytes = await Task.Run(() =>
        {
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
                        // sinais de qualidade do dado (ilegível / inválida) não somem no Excel
                        case "usd": if (i.P.ValorUsd is { } u) { cel.Value = u; cel.Style.NumberFormat.Format = "#,##0.00"; } else if (i.P.ValorTexto != "") cel.Value = i.P.ValorTexto + " (ilegível)"; break;
                        case "taxa": if (i.Taxa is { } t) { cel.Value = t; cel.Style.NumberFormat.Format = "0.0000"; } else cel.Value = "sem taxa"; break;
                        case "brl": if (i.Brl is { } b) { cel.Value = b; cel.Style.NumberFormat.Format = "#,##0.00"; } else if (i.P.ValorUsd is not null) cel.Value = "Não convertido"; break;
                        case "ano": if (i.Ano is { } a) cel.Value = a; break;
                        case "linha": cel.Value = i.P.Linha; break;
                        case "data": if (i.P.Data is { } d) { cel.Value = d; cel.Style.DateFormat.Format = "dd/MM/yyyy"; } else if (i.P.DataTexto != "") cel.Value = i.P.DataTexto + " (inválida)"; break;
                        default: cel.Value = todas[c].Texto(i) == "—" ? "" : todas[c].Texto(i); break;
                    }
                }
            }
            ws.SheetView.FreezeRows(1);
            ws.RangeUsed()?.SetAutoFilter();
            ws.Columns(1, Math.Min(todas.Count, 12)).AdjustToContents();
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        });
        try
        {
            await JS.InvokeVoidAsync("appDownload", $"conversao_propostas_{DateTime.Now:yyyy-MM-dd_HHmm}.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Convert.ToBase64String(bytes));
        }
        catch { /* download indisponível (circuito encerrado): ignora */ }
    }

    // ======================================================================
    //  RELATÓRIO EXECUTIVO (impressão / PDF)
    // ======================================================================
    private bool _relatorio, _menuExp;
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

    // Saindo da página com o relatório aberto: devolve o @page normal (retrato) às outras abas.
    public async ValueTask DisposeAsync()
    {
        if (_relatorio) { try { await JS.InvokeVoidAsync("cvRelatorio", false); } catch { } }
    }

    // ======================================================================
    //  TAXAS DE CÂMBIO (administrador)
    // ======================================================================
    private bool _taxasAberto;
    private string _taxasErro = "", _novoAno = "";
    private readonly Dictionary<int, string> _taxasTxt = new();
    private List<int> _taxasAnos = new();

    /// <summary>Ano sem taxa salva que tem propostas com valor em US$ na base (independe dos filtros).</summary>
    private bool TaxaPendente(int ano) => Cambio.Taxa(ano) is null && _itens.Any(i => i.Ano == ano && i.P.ValorUsd is not null);

    private void SetTaxa(int ano, ChangeEventArgs e) => _taxasTxt[ano] = e.Value?.ToString() ?? "";

    private void AbrirTaxas()
    {
        if (!_isAdmin) return;
        _taxasErro = ""; _novoAno = "";
        var salvas = Cambio.Todas();
        // todos os anos com propostas (inclusive os anteriores a 2024), o ano corrente e os já salvos
        // Faixa contínua só entre anos plausíveis (±15 anos de hoje); um ano digitado errado na planilha
        // (ex.: 1900, 2206) entra como linha avulsa em vez de gerar centenas de linhas.
        var hojeAno = DateTime.Today.Year;
        var anosDados = Conv.Atual.Propostas.Where(p => p.Data is not null).Select(p => p.Data!.Value.Year).Distinct().ToList();
        var plausiveis = anosDados.Where(y => Math.Abs(y - hojeAno) <= 15).ToList();
        var min = Math.Min(plausiveis.DefaultIfEmpty(hojeAno).Min(), hojeAno);
        var max = Math.Max(hojeAno, plausiveis.DefaultIfEmpty(hojeAno).Max());
        _taxasAnos = Enumerable.Range(min, max - min + 1).Union(anosDados).Union(salvas.Keys).Distinct().OrderBy(x => x).ToList();
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
