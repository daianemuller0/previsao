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
    private const string CorG = "#1f9254", CorP = "#6FB3DC", CorA = "#007A9E", CorN = "#93a1b5";
    // Taxa de conversão tem cor própria (azul-marinho); #007A9E fica exclusivo de Abertas.
    private const string CorTx = "#12355B";
    private static readonly ClasseProposta[] Classes =
        { ClasseProposta.Ganha, ClasseProposta.Perdida, ClasseProposta.Aberta, ClasseProposta.NaoClassificada };

    private static string Cor(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => CorG, ClasseProposta.Perdida => CorP, ClasseProposta.Aberta => CorA, _ => CorN,
    };
    private string NomeEn(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => T("Ganhas", "Won"), ClasseProposta.Perdida => T("Perdidas", "Lost"), ClasseProposta.Aberta => T("Abertas", "Open"), _ => T("Não classificadas", "Unclassified"),
    };
    /// <summary>Nome da classe como chave (filtro "Classificação" da Base e exportação Excel): sempre em português.</summary>
    private static string ChaveClasse(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => "Ganha", ClasseProposta.Perdida => "Perdida", ClasseProposta.Aberta => "Aberta", _ => "Não classificada",
    };
    /// <summary>Nome da classe como aparece na tela (no idioma escolhido).</summary>
    private string NomeClasse(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => T("Ganha", "Won"), ClasseProposta.Perdida => T("Perdida", "Lost"), ClasseProposta.Aberta => T("Aberta", "Open"), _ => T("Não classificada", "Unclassified"),
    };
    private string DefClasse(ClasseProposta c) => c switch
    {
        ClasseProposta.Ganha => T("Ganhas: status Closed - Won ou OTP.", "Won: status Closed - Won or OTP."),
        ClasseProposta.Perdida => T("Perdidas: Cancelled, Closed - Customer Lost, Closed - Lost, Closed - No Bid ou Closed - Unknown.",
                                    "Lost: Cancelled, Closed - Customer Lost, Closed - Lost, Closed - No Bid or Closed - Unknown."),
        ClasseProposta.Aberta => T("Abertas: status Open — ainda sem resultado.", "Open: status Open — no outcome yet."),
        _ => T("Não classificadas: status fora da lista combinada, aguardando validação. Ficam fora de ganhas, perdidas e abertas.",
               "Unclassified: status outside the agreed list, pending validation. Excluded from won, lost and open."),
    };
    /// <summary>Classes mostradas nas barras de composição; as não classificadas ficam só nos totais e em Qualidade dos dados.</summary>
    private static readonly ClasseProposta[] ClassesBarra = { ClasseProposta.Ganha, ClasseProposta.Perdida, ClasseProposta.Aberta };
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
    private HashSet<string> _anos = Novo(), _meses = Novo(), _vend = Novo(), _bus = Novo(), _buIcs = Novo(),
        _origens = Novo(), _mercados = Novo(), _paises = Novo(), _produtos = Novo();
    private ClasseProposta? _seg;                              // cross-filter da barra de composição
    private bool _taxaOk => _seg is null;                      // taxa de ganho só vale sem segmento
    private int Ativos => new[] { _anos, _meses, _origens, _vend, _bus, _buIcs, _mercados, _paises, _produtos }.Count(s => s.Count > 0) + (_seg is null ? 0 : 1);
    private IEnumerable<(string, HashSet<string>)> ConjuntosAtivos() =>
        new[] { (T("Ano", "Year"), _anos), (T("Mês", "Month"), _meses), (T("Setor", "Sector"), _origens), (T("Vendedor", "Salesperson"), _vend), ("BU", _bus), ("BU Intercompany", _buIcs), (T("Mercado", "Market"), _mercados), (T("País", "Country"), _paises), (T("Produto", "Product"), _produtos) }.Where(x => x.Item2.Count > 0);
    // Largura mínima de cada chip (rótulo + "…" + ✕) e da faixa de chips: abaixo disso a faixa
    // desce para a linha de baixo, em vez de os chips se sobreporem aos filtros.
    private string ChipTxt(HashSet<string> set) => string.Join(", ", set.Take(2).Select(RotuloValor)) + (set.Count > 2 ? $" +{set.Count - 2}" : "");
    private static string ChipRot(string rot) => rot == "BU Intercompany" ? "BU IC" : rot;
    private int ChipMin(string rot, HashSet<string> set) => 58 + 8 * ChipRot(rot).Length + Math.Min(ChipTxt(set).Length, 4) * 8;
    // Chip curto não encolhe (nada a cortar); o longo pode reduzir até rótulo + 4 letras e "…".
    private string ChipStyle(string rot, HashSet<string> set) =>
        ChipTxt(set).Length <= 4 ? "flex:none" : $"min-width:{ChipMin(rot, set)}px";
    private int ChipsMin() => ConjuntosAtivos().Sum(x => ChipMin(x.Item1, x.Item2) + 6) + (_seg is { } sg ? 64 + 8 * (T("Segmento", "Segment").Length + NomeEn(sg).Length) : 0) + 112;
    private void Limpar() { _anos.Clear(); _meses.Clear(); _vend.Clear(); _bus.Clear(); _buIcs.Clear(); _origens.Clear(); _mercados.Clear(); _paises.Clear(); _produtos.Clear(); _seg = null; _busca = ""; _classes.Clear(); _pag = 0; _limpezas++; }
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
        /// <summary>Base da taxa: true = todas as propostas enviadas (padrão); false = só as decididas.</summary>
        public bool Todas;
        /// <summary>Conversão por valor (botão "Conversão por" da barra de filtros): a taxa principal passa a ser a por valor.</summary>
        public bool PorValor;
        /// <summary>Taxa por quantidade de propostas: ganhas ÷ base.</summary>
        public double? TaxaContagem => Base > 0 ? (double)G / Base * 100 : null;
        /// <summary>Denominador da taxa: decididas, ou todas as propostas.</summary>
        public int Base => Todas ? Total : Decididas;
        /// <summary>A taxa de conversão principal: por quantidade ou por valor, conforme "Conversão por".</summary>
        public double? TaxaQtd => PorValor ? TaxaValor : TaxaContagem;
        public double? TaxaValor => Todas ? (VTotal > 0 ? VG / VTotal * 100 : null) : (VG + VP) > 0 ? VG / (VG + VP) * 100 : null;
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
    private (int, int, bool, double) _chave = (-1, -1, false, 0);

    private void Recalcular()
    {
        var chave = (Conv.Version, Cambio.Version, _emDolar, _taxaTelaAtiva ?? 0);
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
            string MesAbrev(int m) => new DateTime(2000, m, 1).ToString("MMM", CulTela).TrimEnd('.');
            var jan = T("jan", "Jan");
            _prevRotulo = mesMax >= 12 ? (a - 1).ToString() : mesMax == 1 ? $"{jan}/{a - 1}" : $"{jan}\u2060–\u2060{MesAbrev(mesMax)}/{a - 1}"; // word joiner: o rótulo não quebra no traço
        }
    }

    private void Reconstruir()
    {
        var taxas = Cambio.Todas();
        // embutida na Performance Comercial: só os anos da página (2022 em diante), sem as propostas sem data
        _itens = Conv.Atual.Propostas.Where(p => Filtros?.NoPeriodo(p.Data) ?? true).Select(p =>
        {
            int? ano = p.Data?.Year, mes = p.Data?.Month;
            // em R$: NB já vem em R$, sem taxa · AFM: US$ × taxa do ano de envio
            // em US$ (botão Moeda): AFM já vem em US$, sem taxa · NB: R$ ÷ taxa do ano de envio
            if (_dolarAnual)
            {
                double? txu = p.ValorBrl is not null && ano is { } yu && taxas.TryGetValue(yu, out var tu) && tu > 0 ? tu : null;
                double? usd = p.ValorBrl is { } vb ? (txu is { } t3 ? vb / t3 : null) : p.ValorUsd;
                return new Item(p, ano, mes, txu, usd);
            }
            double? tx = p.ValorBrl is null && ano is { } y && taxas.TryGetValue(y, out var t) ? t : null;
            double? brl = p.ValorBrl ?? (p.ValorUsd is { } v && tx is { } t2 ? v * t2 : null);
            // taxa da tela (digitada ao lado de US$): o valor em R$ da tela dividido por ela
            if (_taxaTelaAtiva is double tt && brl is double b0) brl = b0 / tt;
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
        if (exceto != "orig" && _origens.Count > 0) q = q.Where(i => _origens.Contains(i.P.Origem));
        if (exceto != "merc" && _mercados.Count > 0) q = q.Where(i => _mercados.Contains(i.P.Mercado));
        if (exceto != "pais" && _paises.Count > 0) q = q.Where(i => _paises.Contains(i.P.Pais));
        if (exceto != "prod" && _produtos.Count > 0) q = q.Where(i => _produtos.Contains(i.P.Produto));
        return q;
    }

    private Resumo Somar(IEnumerable<Item> itens)
    {
        var r = new Resumo { Todas = _baseTodas, PorValor = _porValor };
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
            if (!i.P.TemValor) r.SemValor++;
            else if (_dolarAnual && i.P.ValorBrl is null) { r.ComUsd[k]++; r.Conv[k]++; }   // em US$: o AFM já vem em US$
            else if (_dolarAnual)
            {
                // em US$: o NB (em R$) precisa da taxa do ano
                r.ComUsd[k]++;
                if (i.Ano is null) r.SemData++;
                else if (i.Brl is null) r.SemTaxa++;
                else r.Conv[k]++;
            }
            else if (i.P.ValorBrl is not null) { r.ComUsd[k]++; r.Conv[k]++; }   // NB: já em R$
            else
            {
                r.ComUsd[k]++;
                var usd = i.P.ValorUsd ?? 0;
                // sem data de envio não há ano, logo não há taxa: são problemas distintos
                if (i.Ano is null) { r.SemData++; r.UsdSemData += usd; }
                else if (i.Brl is null) { r.SemTaxa++; r.UsdSemTaxa += usd; }
                else r.Conv[k]++;
            }
        }
        return r;
    }

    private List<(string Nome, Resumo R)> Grupos(IEnumerable<Item> src, Func<Item, string> chave) =>
        src.GroupBy(chave, StringComparer.OrdinalIgnoreCase)
           .Select(g => (Nome: g.Key, R: Somar(g)))
           .OrderByDescending(x => x.R.VTotal).ThenByDescending(x => x.R.Total).ToList();

    // opções em cascata: cada filtro mostra só o que existe com os OUTROS filtros aplicados
    // embutida na Performance Comercial: o Ano escolhe os anos da página, então lista todos (2022 até hoje)
    private IEnumerable<string> OpAnos => Filtros?.AnoMinimo is int min
        ? Enumerable.Range(min, Math.Max(0, DateTime.Today.Year - min + 1)).Reverse().Select(a => a.ToString())
        : Aplicar("ano").Select(AnoTxt).Distinct().OrderByDescending(x => x == "Sem data" ? "0" : x);
    private IEnumerable<string> OpMeses => Enumerable.Range(1, 12).Select(Fmt.MonthLong);
    private IEnumerable<string> OpVendedores => Aplicar("vend").Select(i => i.P.Vendedor).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
    private IEnumerable<string> OpBus => Aplicar("bu").Select(i => i.P.Bu).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
    // Setor = de qual planilha veio a proposta (AFM ou NB); sempre lista os dois, mesmo se um estiver vazio no recorte
    private IEnumerable<string> OpOrigens => ConversaoService.Origens;
    private IEnumerable<string> OpMercados => Aplicar("merc").Select(i => i.P.Mercado).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
    private IEnumerable<string> OpPaises => Aplicar("pais").Select(i => i.P.Pais).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
    private IEnumerable<string> OpProdutos => Aplicar("prod").Select(i => i.P.Produto).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);
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
    private string R(double v) => Sim + " " + Mon(v);
    /// <summary>O símbolo da moeda da tela (botão "Moeda" da barra de filtros).</summary>
    private string Sim => _emDolar ? "US$" : "R$";
    // ---- idioma: cultura dos números e datas, e valores de filtro na tela ----
    private static readonly System.Globalization.CultureInfo _enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    /// <summary>Cultura dos números e datas na tela: pt-BR (padrão) ou en-US (botão Idioma).</summary>
    private System.Globalization.CultureInfo CulTela => _emIngles ? _enUs : _pt;
    /// <summary>Valor de filtro (guardado em português) como aparece na tela: em inglês, meses e "Sem data" traduzidos.</summary>
    private string RotuloValor(string v)
    {
        if (!_emIngles) return v;
        if (v == "Sem data") return "No date";
        if (v == ConversaoService.NaoIntercompany) return "Not intercompany";
        for (var m = 1; m <= 12; m++)
            if (string.Equals(Fmt.MonthLong(m), v, StringComparison.OrdinalIgnoreCase)) return _enUs.DateTimeFormat.GetMonthName(m);
        return v;
    }

    /// <summary>Valor com a unidade por extenso: 527,7 mi · 1,06 bi · 850 mil (em inglês: 527.7M · 1.06B · 850K).</summary>
    private string Mon(double v)
    {
        var a = Math.Abs(v); var c = CulTela;
        if (a >= 1_000_000_000) return (v / 1_000_000_000).ToString("0.00", c) + T(" bi", "B");
        if (a >= 1_000_000) return (v / 1_000_000).ToString("0.0", c) + T(" mi", "M");
        if (a >= 1_000) return Math.Round(v / 1_000).ToString("#,##0", c) + T(" mil", "K");
        return v.ToString("#,##0", c);
    }
    /// <summary>Rótulo de eixo: marcas redondas sem a casa decimal vazia ("60 mi", não "60,0 mi").</summary>
    private string MonEixo(double v)
    {
        var t = Mon(v);
        if (_emIngles) return t.EndsWith(".0M") ? t[..^3] + "M" : t.EndsWith(".00B") ? t[..^4] + "B" : t;
        return t.EndsWith(",0 mi") ? t[..^5] + " mi" : t.EndsWith(",00 bi") ? t[..^6] + " bi" : t;
    }
    private string P1(double v) => v.ToString("0.0", CulTela) + "%";
    private string N0(int v) => v.ToString("N0", CulTela);
    private string PctDe(double parte, double todo) => todo > 0 ? P1(parte / todo * 100) : "—";
    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    private static string EscTip(string s) => Esc(s).Replace("\n", "&#10;");
    private static string F(double v) => v.ToString("0.##", Inv);
    private string Pp(double v) => Math.Abs(v).ToString("0.0", CulTela) + T(" p.p.", " pp");

    // Estados dos valores: R$ 0 (zero real) ≠ — (sem informação) ≠ Não convertido (sem taxa) ≠ Não calculável.
    private string Estado(int comUsd, int conv, double v) =>
        comUsd == 0 ? "—" : conv == 0 ? T("Não convertido", "Not converted") : R(v) + (conv < comUsd ? "*" : "");
    private string EstadoTip(int comUsd, int conv, double v) =>
        comUsd == 0 ? T("Nenhuma proposta com valor em US$ informado.", "No proposal with a US$ value entered.")
        : conv == 0 ? T("Há valor em US$, mas falta a taxa de câmbio do ano da proposta (ou a data de envio).",
                        "There is a US$ value, but the exchange rate for the proposal's year (or the sent date) is missing.")
        : Sim + " " + v.ToString("#,##0", CulTela) + (conv < comUsd ? T($" · parcial: {comUsd - conv} proposta(s) sem taxa de câmbio ou sem data de envio ficaram de fora.",
                                                                       $" · partial: {comUsd - conv} proposal(s) without an exchange rate or sent date were left out.") : "");

    private string Din(Resumo r, ClasseProposta c) => Estado(r.ComUsd[(int)c], r.Conv[(int)c], r.Val(c));
    private string DinTip(Resumo r, ClasseProposta c) => EstadoTip(r.ComUsd[(int)c], r.Conv[(int)c], r.Val(c));
    private string DinTotal(Resumo r) => Estado(r.TotComUsd, r.TotConv, r.VTotal);
    private string DinTotalTip(Resumo r) => EstadoTip(r.TotComUsd, r.TotConv, r.VTotal);

    /// <summary>A taxa principal (quantidade ou valor, conforme "Conversão por").</summary>
    private string TxQtd(Resumo r) => !_taxaOk ? "—" : TxQtdGlobal(r);
    private string TxQtdGlobal(Resumo r) => r.PorValor ? TxValorGlobal(r) : TxContagemGlobal(r);
    private string TxContagemGlobal(Resumo r) => r.Base == 0 ? T("Não calculável", "Not calculable") : P1(r.TaxaContagem!.Value);
    /// <summary>A outra medida, mostrada ao lado da principal: por valor (se a principal é por quantidade) ou o contrário.</summary>
    private string TxOutra(Resumo r) => !_taxaOk ? "—" : TxOutraGlobal(r);
    private string TxOutraGlobal(Resumo r) => r.PorValor ? TxContagemGlobal(r) : TxValorGlobal(r);
    private string OutraRot => _porValor ? T("em quantidade", "by count") : T("em valor", "by value");
    private string TxValor(Resumo r) => !_taxaOk ? "—" : TxValorGlobal(r);
    private string TxValorGlobal(Resumo r)
    {
        var usd = r.ComUsd[(int)ClasseProposta.Ganha] + r.ComUsd[(int)ClasseProposta.Perdida];
        var conv = r.Conv[(int)ClasseProposta.Ganha] + r.Conv[(int)ClasseProposta.Perdida];
        if (usd == 0) return "—";
        if (conv == 0) return T("Não convertido", "Not converted");
        if ((r.Todas ? r.VTotal : r.VG + r.VP) <= 0) return T("Não calculável", "Not calculable");
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
        if (_prev is null) return (T($"Sem dados comparáveis em {_prevRotulo}", $"No comparable data in {_prevRotulo}"), "neu");
        if (_r.TaxaQtd is not { } cur || _prev.TaxaQtd is not { } ant) return (T($"Sem propostas decididas para comparar com {_prevRotulo}", $"No decided proposals to compare with {_prevRotulo}"), "neu");
        var d = cur - ant;
        if (Math.Abs(d) < 0.05) return (T($"Estável vs {_prevRotulo} (0,0 p.p.)", $"Stable vs {_prevRotulo} (0.0 pp)"), "neu");
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
            anos = ds.Count == 0 ? T("Todos os anos", "All years") : $"{ds.Min().ToString("MMM/yyyy", CulTela)}{T(" a ", " to ")}{ds.Max().ToString("MMM/yyyy", CulTela)}";
        }
        // meses em ordem de calendário, não na ordem em que foram marcados
        var ms = Enumerable.Range(1, 12).Select(Fmt.MonthLong).Where(_meses.Contains).ToList();
        var meses = ms.Count == 0 ? "" : " · " + string.Join(", ", ms.Take(3).Select(RotuloValor)) + (ms.Count > 3 ? $" +{ms.Count - 3}" : "");
        return anos + meses;
    }

    private string NotasFiltros()
    {
        var l = ConjuntosAtivos().Select(x => $"{x.Item1}: {string.Join(", ", x.Item2.Take(3).Select(RotuloValor))}{(x.Item2.Count > 3 ? "…" : "")}").ToList();
        return l.Count == 0 ? T("Sem filtros adicionais", "No additional filters") : string.Join(" · ", l);
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
        if (meses.Count == 0) return Vazio(this.T("Nenhuma proposta com data de envio no recorte.", "No proposals with a sent date in the selection."));
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
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='{this.T("taxa de conversão mensal", "monthly conversion rate")}'>");
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
            var tip = $"{meses[i].ToString("MMMM/yyyy", CulTela)}\n" +
                      (r is null ? this.T("Sem propostas enviadas", "No proposals sent") :
                       this.T($"taxa de conversão: {(r.Decididas > 0 ? P1(r.TaxaQtd!.Value) : "não calculável")} ({N0(r.G)} ganhas de {N0(r.Decididas)} decididas)\n",
                         $"conversion rate: {(r.Decididas > 0 ? P1(r.TaxaQtd!.Value) : "not calculable")} ({N0(r.G)} won of {N0(r.Decididas)} decided)\n") +
                       this.T($"Perdidas: {N0(r.P)} · Abertas: {N0(r.A)} ({PctDe(r.A, r.Total)} do mês)", $"Lost: {N0(r.P)} · Open: {N0(r.A)} ({PctDe(r.A, r.Total)} of the month)") +
                       (r.NC > 0 ? this.T($" · Não classificadas: {N0(r.NC)}", $" · Unclassified: {N0(r.NC)}") : "")) +
                      (ra is not null && ra.Decididas > 0 ? this.T($"\nMesmo mês de {meses[i].Year - 1}: {P1(ra.TaxaQtd!.Value)} ({N0(ra.Decididas)} decididas)",
                                                              $"\nSame month of {meses[i].Year - 1}: {P1(ra.TaxaQtd!.Value)} ({N0(ra.Decididas)} decided)") : "");
            sb.Append($"<rect x='{F(L + passo * i)}' y='{F(T)}' width='{F(passo)}' height='{F(H - T - B + 6)}' fill='transparent'><title>{EscTip(tip)}</title></rect>");
            if (!soPares || meses[i].Month % 2 == 0)
                sb.Append($"<text x='{F(x)}' y='{F(H - 8)}' text-anchor='middle' font-size='{F(fs)}' fill='#3b4a61'>{Esc(meses[i].ToString("MMM/yy", CulTela))}</text>");
        }
        sb.Append($"<text x='{F(L - 8)}' y='{F(baseStrip - strip + 6)}' text-anchor='end' font-size='{F(compact ? 10.5 : 9.5)}' fill='#6b7a90'>{maxD}</text>");
        sb.Append($"<text x='{F(L - 8)}' y='{F(baseStrip)}' text-anchor='end' font-size='{F(compact ? 10.5 : 9.5)}' fill='#6b7a90'>0</text>");
        sb.Append("</svg>");
        sb.Append("<div class='cvx-leg'>");
        sb.Append($"<span><i class='cvx-ln' style='border-top-color:{CorTx}'></i>{this.T("Taxa de conversão do mês", "Conversion rate for the month")}</span>");
        if (comparar) sb.Append($"<span><i class='cvx-ln dash'></i>{this.T("Mesmo mês do ano anterior", "Same month of the previous year")}</span>");
        sb.Append($"<span><i class='cvx-pt' style='border-color:{CorTx}'></i>{this.T("Mais de 30% das propostas do mês ainda abertas (a taxa pode mudar)", "More than 30% of the month's proposals still open (the rate may change)")}</span>");
        sb.Append($"<span><i class='cvx-bx'></i>{this.T("Propostas decididas (escala própria, embaixo)", "Decided proposals (own scale, below)")}</span>");
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
        if (por.Count == 0) return Vazio(this.T("Nenhuma proposta com data de envio em " + ano + ".", "No proposals with a sent date in " + ano + "."));
        // valor nunca vira zero silencioso: sem taxa do ano, o gráfico diz isso em vez de barras vazias
        if (!qtd && por.Values.All(r => r.TotConv == 0))
            return Vazio(por.Values.Any(r => r.TotComUsd > 0)
                ? this.T($"Valores de {ano} não convertidos: falta a taxa USD/BRL do ano.", $"{ano} values not converted: the USD/BRL rate for the year is missing.")
                : this.T($"Nenhuma proposta com valor em US$ em {ano}.", $"No proposals with a US$ value in {ano}."));
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
        // W = largura real do cartão (px): o texto fica com o tamanho escrito; a altura acompanha com limites
        double W = _chartW, H = Math.Round(Math.Clamp(W * .30, 320, 400)), L = 84, Rr = 18, T = 44, B = 36;
        var passo = (W - L - Rr) / meses.Count; var larg = Math.Min(passo * 0.6, 56);
        double Y(double v) => T + (H - T - B) * (1 - v / maxY);
        string Rot(double v) => qtd ? N0((int)Math.Round(v)) : MonEixo(v);

        var sb = new StringBuilder("<div class='chart-body'>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='{(qtd ? this.T("Volume mensal", "Monthly volume") : this.T("Valor mensal", "Monthly value"))}'>");
        if (!qtd) sb.Append($"<text x='{F(L - 8)}' y='{F(T - 16)}' text-anchor='end' font-size='12' font-weight='700' fill='#6b7a90'>{Sim}</text>");
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
                    var tip = this.T($"{NomeEn(cls)} em {meses[i].ToString("MMM/yyyy", _pt)}\n{N0(r.Qtd(cls))} proposta(s) · {Din(r, cls)}",
                                     $"{NomeEn(cls)} in {meses[i].ToString("MMM/yyyy", _enUs)}\n{N0(r.Qtd(cls))} proposal(s) · {Din(r, cls)}");
                    sb.Append($"<rect class='cv-seg' x='{F(x)}' y='{F(Y(acum + v))}' width='{F(larg)}' height='{F(Y(acum) - Y(acum + v))}' fill='{Cor(cls)}' rx='2'><title>{EscTip(tip)}</title></rect>");
                    acum += v;
                }
                var parcial = !qtd && r.TotConv > 0 && r.TotConv < r.TotComUsd;
                if (parcial) temParcial = true;
                if (acum > 0)
                    sb.Append($"<text x='{F(x + larg / 2)}' y='{F(Y(acum) - 6)}' text-anchor='middle' font-size='12.5' font-weight='700' fill='#3b4a61'>{Esc(qtd ? N0((int)acum) : Mon(acum) + (parcial ? "*" : ""))}</text>");
                else if (!qtd && r.TotComUsd > 0 && r.TotConv == 0)
                    sb.Append($"<text x='{F(x + larg / 2)}' y='{F(Y(0) - 6)}' text-anchor='middle' font-size='11' fill='#c0392b'>n/c<title>{this.T("Não convertido: falta a taxa de câmbio", "Not converted: the exchange rate is missing")}</title></text>");
            }
            sb.Append($"<text x='{F(x + larg / 2)}' y='{F(H - 12)}' text-anchor='middle' font-size='12.5' font-weight='600' fill='#8a97ab' letter-spacing='.06em'>{Esc(meses[i].ToString("MMM", CulTela).TrimEnd('.').ToUpper(CulTela))}</text>");
        }
        sb.Append("</svg><div class='cvx-leg'>");
        for (var c = 0; c < cats; c++) sb.Append($"<span><i class='cvx-dot' style='background:{Cor(Classes[c])}'></i>{NomeEn(Classes[c])}</span>");
        sb.Append("</div>");
        if (temParcial) sb.Append($"<p class='cvx-note'>{this.T("* mês com propostas sem taxa de câmbio (valor parcial)", "* month with proposals without an exchange rate (partial value)")}</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private string _metTrend = "winrate";    // winrate | volume | valor
    private RenderFragment Tendencia(int offset, bool compact = false) => builder =>
    {
        string html;
        if (_metTrend == "winrate" && !_taxaOk)
            html = Vazio(T("taxa de conversão não se aplica com um segmento selecionado na barra de composição. Remova o segmento para ver a taxa.",
                           "The conversion rate does not apply with a segment selected in the composition bar. Remove the segment to see the rate."));
        else html = SvgAnual(_metTrend == "winrate" ? _f : _fx, _metTrend);
        builder.AddMarkupContent(0, html);
    };

    /// <summary>Uma linha por ano de envio, janeiro a dezembro. A métrica (taxa, quantidade ou valor) vem do botão do topo; nunca mistura escalas.</summary>
    private string SvgAnual(IEnumerable<Item> src, string met)
    {
        var com = src.Where(i => i.Ano is not null && i.Mes is not null).ToList();
        if (com.Count == 0) return Vazio(this.T("Nenhuma proposta com data de envio no recorte.", "No proposals with a sent date in the selection."));
        var por = com.GroupBy(i => (A: i.Ano!.Value, M: i.Mes!.Value)).ToDictionary(g => g.Key, g => Somar(g));
        var anos = por.Keys.Select(k => k.A).Distinct().OrderByDescending(a => a).ToList();
        double? Val(int a, int m)
        {
            if (!por.TryGetValue((a, m), out var r)) return null;
            return met == "winrate" ? (r.Total > 0 ? r.G * 100.0 / r.Total : null) : met == "volume" ? r.Total : (r.TotConv > 0 ? r.VTotal : null);
        }
        string Rot(double v) => met == "winrate" ? F(v) + "%" : met == "volume" ? N0((int)Math.Round(v)) : Mon(v);
        string Txt(int a, int m)
        {
            if (!por.TryGetValue((a, m), out var r)) return this.T("sem propostas", "no proposals");
            return met == "winrate" ? (r.Total > 0 ? this.T($"{P1(r.G * 100.0 / r.Total)} ({N0(r.G)} ganhas de {N0(r.Total)} enviadas · {N0(r.P)} perdidas, {N0(r.A)} abertas)",
                                                            $"{P1(r.G * 100.0 / r.Total)} ({N0(r.G)} won of {N0(r.Total)} sent · {N0(r.P)} lost, {N0(r.A)} open)") : this.T("sem propostas enviadas", "no proposals sent"))
                 : met == "volume" ? this.T($"{N0(r.Total)} propostas · {N0(r.G)} ganhas, {N0(r.P)} perdidas, {N0(r.A)} abertas", $"{N0(r.Total)} proposals · {N0(r.G)} won, {N0(r.P)} lost, {N0(r.A)} open")
                 : (r.TotConv > 0 ? this.T($"{R(r.VTotal)} · ganho {Din(r, ClasseProposta.Ganha)}, perdido {Din(r, ClasseProposta.Perdida)}, aberto {Din(r, ClasseProposta.Aberta)}",
                                           $"{R(r.VTotal)} · won {Din(r, ClasseProposta.Ganha)}, lost {Din(r, ClasseProposta.Perdida)}, open {Din(r, ClasseProposta.Aberta)}")
                                  : this.T("valor não convertido (sem taxa de câmbio)", "value not converted (no exchange rate)"));
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
        // W = largura real do cartão (px): o texto fica com o tamanho escrito; a altura acompanha com limites
        double W = _chartW, H = Math.Round(Math.Clamp(W * .31, 330, 430)), L = 70, Rr = 30, T = 44, B = 40;
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
            return rr is not null && rr.Total > 0 && (double)rr.A / rr.Total > .30;
        }
        var temFraco = Enumerable.Range(1, 12).Any(m => Val(anos[0], m) is not null && Fraco(anos[0], m));

        var sb = new StringBuilder("<div class='chart-body cvx-anual'><div class='cvx-leg top'>");
        for (var k = 0; k < anos.Count; k++)
            sb.Append($"<span><i class='cvx-ln{(k == 1 ? " dash" : k >= 2 ? " dash dot" : "")}' style='border-top-color:{CorAno(k)}'></i>{anos[k]}</span>");
        if (anos.Count > cores.Length)
            sb.Append($"<span class='cvx-leg-aviso'>{this.T($"{anos.Count} anos no recorte: de {anos[^1]} a {anos[cores.Length - 1]} na mesma cor cinza · filtre por Ano para comparar menos anos", $"{anos.Count} years in the selection: {anos[^1]} to {anos[cores.Length - 1]} in the same grey · filter by Year to compare fewer years")}</span>");
        if (temFraco)
            sb.Append($"<span><i class='cvx-pt' style='border-color:{cores[0]}'></i>{(met == "winrate" ? this.T("Mês em andamento ou mais de 30% das propostas ainda abertas: a taxa pode subir", "Month in progress or more than 30% of proposals still open: the rate may rise") : this.T("Mês em andamento: o valor pode mudar", "Month in progress: the value may change"))}</span>");
        sb.Append("</div>");
        sb.Append($"<svg viewBox='0 0 {F(W)} {F(H)}' role='img' aria-label='{this.T("evolução mensal por ano", "monthly trend by year")}' preserveAspectRatio='xMidYMid meet'>");
        sb.Append($"<defs><linearGradient id='cvArea' x1='0' y1='0' x2='0' y2='1'><stop offset='0%' stop-color='{cores[0]}' stop-opacity='.18'/><stop offset='100%' stop-color='{cores[0]}' stop-opacity='0'/></linearGradient></defs>");
        // título do eixo: a unidade fica explícita (taxa, quantidade ou R$)
        sb.Append($"<text x='{F(L)}' y='{F(T - 26)}' font-size='12' font-weight='700' fill='#6b7a90'>{(met == "winrate" ? this.T("Taxa de conversão (% das propostas enviadas no mês)", "Conversion rate (% of proposals sent in the month)") : met == "volume" ? this.T("Propostas enviadas", "Proposals sent") : this.T($"Valor ({Sim})", $"Value ({Sim})"))}</text>");
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
            sb.Append($"<text x='{F(X(m))}' y='{F(H - 14)}' text-anchor='middle' font-size='12.5' font-weight='{(m == mesAtual ? 700 : 600)}' fill='{(m == mesAtual ? cores[0] : "#8a97ab")}' letter-spacing='.06em'>{Esc(new DateTime(2000, m, 1).ToString("MMM", CulTela).TrimEnd('.').ToUpper(CulTela))}</text>");

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
                var tip = EscTip($"{a} · {new DateTime(2000, m, 1).ToString("MMMM", CulTela)}\n{Txt(a, m)}");
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
            sb.Append($"<rect x='{F(L + passo * (m - 1))}' y='{F(T)}' width='{F(passo)}' height='{F(H - T - B)}' fill='transparent'><title>{EscTip(new DateTime(2000, m, 1).ToString("MMMM", CulTela) + "\n" + linhas)}</title></rect>");
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
        var baseBarra = Math.Max(r.G + r.P + r.A, 1);
        var sb = new StringBuilder("<div class='cvx-stackbar static'>");
        foreach (var c in ClassesBarra)
        {
            var n = r.Qtd(c); if (n == 0) continue;
            var pct = Largura(n, r.Total);
            sb.Append($"<span class='cvx-seg {ClasseCss(c)}' style='width:{F(n * 100.0 / baseBarra)}%;background:{Cor(c)}' title='{EscTip($"{NomeEn(c)}: {N0(n)} ({PctDe(n, r.Total)})\n{DefClasse(c)}")}'>{(pct >= 8 ? Esc(P1(pct)) : "")}</span>");
        }
        sb.Append("</div><div class='cvx-legend'>");
        foreach (var c in ClassesBarra)
            sb.Append($"<span><i class='cvx-dot' style='background:{Cor(c)}'></i>{NomeEn(c)} <b>{N0(r.Qtd(c))}</b> · {PctDe(r.Qtd(c), r.Total)}</span>");
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
                    ComUsd: its.Count(i => i.P.TemValor), Conv: its.Count(i => i.Brl is not null));
        });
        return (qtd ? l.OrderByDescending(x => x.Qtd).ThenByDescending(x => x.Valor) : l.OrderByDescending(x => x.Valor).ThenByDescending(x => x.Qtd)).ToList();
    }

    private string _metPerda = "qtd";
    /// <summary>Pareto horizontal: barra por status, participação e % acumulado.</summary>
    private string HtmlPareto(bool qtd, bool comSegmento = true)
    {
        var linhas = LinhasPareto(qtd, comSegmento);
        var totQ = linhas.Sum(x => x.Qtd); var totV = linhas.Sum(x => x.Valor);
        if (totQ == 0) return Vazio(T("Nenhuma proposta perdida no recorte.", "No lost proposals in the selection."));
        var max = Math.Max(linhas.Max(x => qtd ? x.Qtd : x.Valor), 0.0000001);
        var total = qtd ? totQ : totV;
        var sb = new StringBuilder("<div class='ibar cvx-ibar cvx-static'>");
        double acum = 0; var pos = 0;
        foreach (var l in linhas.Where(x => x.Qtd > 0))
        {
            var v = qtd ? l.Qtd : l.Valor; acum += v; pos++;
            var valorTxt = qtd ? N0(l.Qtd) + T(" propostas", " proposals") : Estado(l.ComUsd, l.Conv, l.Valor);
            var tip = T($"{l.Nome}\n{N0(l.Qtd)} proposta(s) · {EstadoTip(l.ComUsd, l.Conv, l.Valor)}\nAcumulado: {PctDe(acum, total)}",
                        $"{l.Nome}\n{N0(l.Qtd)} proposal(s) · {EstadoTip(l.ComUsd, l.Conv, l.Valor)}\nCumulative: {PctDe(acum, total)}");
            sb.Append(IbarItem(pos, l.Nome, Esc(valorTxt), Esc(PctDe(v, total)), v / max * 100, tip,
                extra: $"<span class='cvx-ibm' title='{T("Participação acumulada (Pareto)", "Cumulative share (Pareto)")}'>{Esc(PctDe(acum, total))} <small>{T("acumulado", "cumulative")}</small></span><span class='ibar-sep'></span>"));
        }
        sb.Append("</div>");
        var zeros = linhas.Where(x => x.Qtd == 0).Select(x => x.Nome).ToList();
        if (zeros.Count > 0) sb.Append($"<p class='cvx-note'>{T("Sem ocorrências no recorte: ", "No occurrences in the selection: ")}{Esc(string.Join(", ", zeros))}</p>");
        return sb.ToString();
    }

    private string TextoInsightPerda(bool comSegmento = true)
    {
        var linhas = LinhasPareto(true, comSegmento);
        var tot = linhas.Sum(x => x.Qtd);
        if (tot == 0) return T("Sem propostas perdidas no recorte atual.", "No lost proposals in the current selection.");
        var top = linhas.First();
        var totV = linhas.Sum(x => x.Valor);
        var s = T($"{top.Nome} representa {PctDe(top.Qtd, tot)} das propostas classificadas como perdidas ({N0(top.Qtd)} de {N0(tot)}).",
                  $"{top.Nome} accounts for {PctDe(top.Qtd, tot)} of the proposals classified as lost ({N0(top.Qtd)} of {N0(tot)}).");
        var topV = LinhasPareto(false, comSegmento).First();
        if (totV > 0 && topV.Nome != top.Nome) s += T($" Em valor, o maior impacto é de {topV.Nome} ({PctDe(topV.Valor, totV)} do valor perdido convertido).",
                                                     $" By value, the largest impact is {topV.Nome} ({PctDe(topV.Valor, totV)} of the converted lost value).");
        else if (totV > 0) s += T($" Em valor, também lidera, com {PctDe(topV.Valor, totV)} do valor perdido convertido.",
                                  $" By value, it also leads, with {PctDe(topV.Valor, totV)} of the converted lost value.");
        return s;
    }

    private string HtmlVendTop(int n, int minDec)
    {
        var l = Grupos(_f, i => i.P.Vendedor).Where(x => x.R.Decididas >= minDec && x.R.TaxaQtd is not null)
            .OrderByDescending(x => x.R.TaxaQtd).ThenByDescending(x => x.R.Decididas).Take(n).ToList();
        if (l.Count == 0) return Vazio(T($"Nenhum vendedor com {minDec}+ propostas decididas no recorte.", $"No salesperson with {minDec}+ decided proposals in the selection."));
        var sb = new StringBuilder($"<table class='cvx-mini-tbl'><thead><tr><th>#</th><th>{T("Vendedor", "Salesperson")}</th><th class='r'>{T("Taxa de conversão", "Conversion rate")}</th><th class='r'>{T("Ganhas", "Won")}</th><th class='r'>{T("Perdidas", "Lost")}</th><th class='r'>{T("Abertas", "Open")}</th><th class='r'>{T("Decididas", "Decided")}</th><th class='r'>{T("Valor ganho", "Won value")}</th></tr></thead><tbody>");
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
        if (l.Count == 0) return Vazio(T($"Nenhuma unidade com {minDec}+ propostas decididas.", $"No unit with {minDec}+ decided proposals."));
        var sb = new StringBuilder("<div class='ibar cvx-ibar cvx-static'>");
        var pos = 0;
        foreach (var (nome, r) in l)
        {
            pos++;
            sb.Append(IbarItem(pos, nome, Esc(P1(r.TaxaQtd!.Value)), $"{N0(r.Decididas)}{T(" decididas", " decided")}", r.TaxaQtd.Value,
                T($"{nome}\n{N0(r.G)} ganhas de {N0(r.Decididas)} decididas", $"{nome}\n{N0(r.G)} won of {N0(r.Decididas)} decided"), marca: _r.TaxaQtd));
        }
        sb.Append("</div>");
        if (fora > 0) sb.Append($"<p class='rpt-small'>{T($"{fora} unidade(s) com menos de {minDec} decididas ou além das {max} primeiras não aparecem.", $"{fora} unit(s) with fewer than {minDec} decided or beyond the top {max} are not shown.")}</p>");
        return sb.ToString();
    }

    /// <summary>
    /// Uma linha de barra horizontal no padrão da Visão Executiva (.ibar): posição, nome e métricas
    /// em cima, barra grossa embaixo. <paramref name="largura"/> em % (0–100); <paramref name="marca"/>
    /// desenha a linha vertical de referência (média) sobre a barra.
    /// </summary>
    private static string IbarItem(int pos, string nome, string valorHtml, string shareHtml, double largura, string tip,
                                   string extra = "", string sub = "", double? marca = null)
    {
        var w = double.IsFinite(largura) ? Math.Clamp(largura, 0, 100) : 0;
        var sb = new StringBuilder($"<div class='ibar-item{(pos is >= 1 and <= 3 ? $" top{pos}" : "")}' title='{EscTip(tip)}'><div class='ibar-head'>");
        if (pos > 0) sb.Append($"<span class='ibar-num'>{pos}</span>");
        sb.Append($"<span class='ibar-name'>{Esc(nome)}</span><span class='ibar-metrics'>{extra}<span class='ibar-val'>{valorHtml}</span><span class='ibar-share'>{shareHtml}</span></span></div>");
        sb.Append($"<div class='ibar-track cvx-ib-track'><span class='ibar-fill' style='width:{F(w)}%'></span>");
        if (marca is { } m) sb.Append($"<em class='cvx-ib-mk' style='left:{F(Math.Clamp(m, 0, 100))}%'></em>");
        sb.Append("</div>");
        if (sub != "") sb.Append($"<div class='cvx-ib-sub'>{sub}</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Distribuição financeira: barra de composição por valor (só do que foi convertido).</summary>
    private string HtmlComposicaoValor(Resumo r)
    {
        if (r.TotConv == 0) return Vazio(r.TotComUsd == 0 ? T("Nenhuma proposta com valor em US$ informado.", "No proposal with a US$ value entered.")
                                                          : T("Valores não convertidos: falta a taxa de câmbio do ano das propostas.", "Values not converted: the exchange rate for the proposals' year is missing."));
        var tot = Math.Max(r.VTotal, 0.0000001);
        var baseBarra = Math.Max(r.VG + r.VP + r.VA, 0.0000001);
        var sb = new StringBuilder("<div class='cvx-stackbar static'>");
        foreach (var c in ClassesBarra)
        {
            var v = r.Val(c); if (v <= 0) continue;
            var pct = v / tot * 100;
            sb.Append($"<span class='cvx-seg {ClasseCss(c)}' style='width:{F(v / baseBarra * 100)}%;background:{Cor(c)}' title='{EscTip($"{NomeEn(c)}: {DinTip(r, c)}")}'>{(pct >= 9 ? Esc(P1(pct)) : "")}</span>");
        }
        sb.Append("</div><div class='cvx-legend'>");
        foreach (var c in ClassesBarra)
        {
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
        if (l.Count == 0) return Vazio(semConv > 0 ? T("Valores por unidade não convertidos: falta a taxa USD/BRL do ano.", "Values by unit not converted: the USD/BRL rate for the year is missing.")
                                                   : T("Sem valores convertidos por unidade no recorte.", "No converted values by unit in the selection."));
        var max = Math.Max(l.Max(x => x.R.VTotal), 0.0000001);
        var sb = new StringBuilder("<div class='ibar cvx-ibar cvx-static scroll-y'>");
        var pos = 0;
        foreach (var (nome, r) in l)
        {
            pos++;
            var tip = T($"{nome}\nGanho: {Din(r, ClasseProposta.Ganha)}\nPerdido: {Din(r, ClasseProposta.Perdida)}\nAberto: {Din(r, ClasseProposta.Aberta)}",
                        $"{nome}\nWon: {Din(r, ClasseProposta.Ganha)}\nLost: {Din(r, ClasseProposta.Perdida)}\nOpen: {Din(r, ClasseProposta.Aberta)}");
            var det = $"<span class='cvx-ibm g' title='{T("Valor ganho", "Won value")}'>{Esc(Din(r, ClasseProposta.Ganha))} <small>{T("ganho", "won")}</small></span>"
                    + $"<span class='cvx-ibm p' title='{T("Valor perdido", "Lost value")}'>{Esc(Din(r, ClasseProposta.Perdida))} <small>{T("perdido", "lost")}</small></span>"
                    + $"<span class='cvx-ibm a' title='{T("Valor em aberto", "Open value")}'>{Esc(Din(r, ClasseProposta.Aberta))} <small>{T("aberto", "open")}</small></span>";
            sb.Append(IbarItem(pos, nome, Esc(DinTotal(r)), Esc(PctDe(r.VTotal, soma)), r.VTotal / max * 100, tip, sub: det));
        }
        sb.Append("</div>");
        if (semConv > 0) sb.Append($"<p class='cvx-note'>{T($"{semConv} unidade(s) com valor em US$ sem taxa de câmbio não aparecem.", $"{semConv} unit(s) with a US$ value but no exchange rate are not shown.")}</p>");
        if (todos.Count > 10) sb.Append($"<p class='cvx-note'>{T($"Top 10 de {todos.Count} unidades; participação sobre o total do recorte.", $"Top 10 of {todos.Count} units; share of the selection total.")}</p>");
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
    private static IEnumerable<Item> SoIc(IEnumerable<Item> src) => src.Where(i => i.P.BuIc != ConversaoService.NaoIntercompany);
    private Resumo ResumoIc() => Somar(SoIc(_fx));

    private List<(string Nome, Resumo R)> UnidadesOrdenadas(Func<Item, string> sel, string exceto)
    {
        var src = Aplicar(exceto);
        if (_seg is { } s) src = src.Where(i => i.P.Classe == s);
        // BU Intercompany: propostas que não são intercompany não entram no ranking das unidades IC
        if (exceto == "buic") src = src.Where(i => i.P.BuIc != ConversaoService.NaoIntercompany);
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

    /// <summary>Em US$ (botão Moeda), a coluna do valor convertido e a da taxa falam da conversão para dólar.</summary>
    /// <summary>Em inglês, títulos e textos da tela vêm daqui; Col.Titulo e Col.Texto ficam em português (busca em PT e exportação Excel).</summary>
    private string TituloCol(Col c)
    {
        if (_emDolar && c.Key == "brl") return T("Valor convertido (US$)", "Converted value (US$)");
        if (!_emIngles) return c.Titulo;
        return c.Key switch
        {
            "origem" => "Sector", "data" => "Sent date", "ano" => "Year", "mes" => "Month", "vend" => "Salesperson",
            "cliente" => "Client", "mercado" => "Market", "pais" => "Country", "produto" => "Product", "bu" => "BU",
            "buic" => "BU Intercompany", "status" => "Original status", "classe" => "Classification",
            "usd" => "Original value (US$)", "taxa" => "Applied rate", "brl" => "Converted value (R$)", "linha" => "Spreadsheet row",
            _ => c.Titulo,
        };
    }
    private string TextoCol(Col c, Item i)
    {
        if (!_emIngles)
            return !_emDolar ? c.Texto(i) : c.Key switch
            {
                "brl" => i.Brl is { } b ? b.ToString("#,##0.00", _pt) : (!i.P.TemValor ? (i.P.ValorTexto == "" ? "—" : i.P.ValorTexto + " (ilegível)") : "Não convertido"),
                "taxa" => i.P.ValorBrl is null ? "já em US$" : i.Taxa is { } t ? "R$ ÷ " + t.ToString("0.0000", _pt) : "sem taxa",
                _ => c.Texto(i),
            };
        // em inglês: os mesmos valores, com textos em inglês e números no formato americano
        var cu = _enUs;
        return c.Key switch
        {
            "data" => i.P.Data?.ToString("dd MMM yyyy", cu) ?? (i.P.DataTexto == "" ? "—" : i.P.DataTexto + " (invalid)"),
            "mes" => i.Mes is { } m ? cu.DateTimeFormat.GetMonthName(m) : "—",
            "classe" => NomeClasse(i.P.Classe),
            "buic" => RotuloValor(i.P.BuIc),
            "usd" => i.P.ValorUsd is { } v ? v.ToString("#,##0.00", cu) : i.P.ValorBrl is not null ? "—" : (i.P.ValorTexto == "" ? "—" : i.P.ValorTexto + " (unreadable)"),
            "taxa" => _emDolar
                ? (i.P.ValorBrl is null ? "already in US$" : i.Taxa is { } t ? "R$ ÷ " + t.ToString("0.0000", cu) : "no rate")
                : (i.P.ValorBrl is not null ? "already in R$" : i.Taxa is { } t2 ? t2.ToString("0.0000", cu) : "no rate"),
            "brl" => i.Brl is { } b ? b.ToString("#,##0.00", cu) : (!i.P.TemValor ? (i.P.ValorTexto == "" ? "—" : i.P.ValorTexto + " (unreadable)") : "Not converted"),
            _ => c.Texto(i),
        };
    }

    private List<Col> ColunasPadrao() => new()
    {
        new("origem", "Setor", "center", i => i.P.Origem, i => i.P.Origem),
        new("data", "Data de envio", "nowrap", i => i.P.Data?.ToString("dd/MM/yyyy") ?? (i.P.DataTexto == "" ? "—" : i.P.DataTexto + " (inválida)"), i => i.P.Data),
        new("ano", "Ano", "center", i => i.Ano?.ToString() ?? "—", i => i.Ano),
        new("mes", "Mês", "center", i => i.Mes is { } m ? Fmt.MonthLong(m) : "—", i => i.Mes),
        new("vend", "Vendedor", "", i => i.P.Vendedor, i => i.P.Vendedor),
        new("cliente", "Cliente", "", i => i.P.Cliente, i => i.P.Cliente),
        new("mercado", "Mercado", "", i => i.P.Mercado, i => i.P.Mercado),
        new("pais", "País", "", i => i.P.Pais, i => i.P.Pais),
        new("produto", "Produto", "", i => i.P.Produto, i => i.P.Produto),
        new("bu", "BU", "", i => i.P.Bu, i => i.P.Bu),
        new("buic", "BU Intercompany", "", i => i.P.BuIc, i => i.P.BuIc),
        new("status", "Status original", "", i => i.P.StatusOriginal == "" ? "—" : i.P.StatusOriginal, i => i.P.StatusOriginal),
        new("classe", "Classificação", "center", i => ChaveClasse(i.P.Classe), i => (int)i.P.Classe),
        new("usd", "Valor original (US$)", "right", i => i.P.ValorUsd is { } v ? v.ToString("#,##0.00", _pt) : i.P.ValorBrl is not null ? "—" : (i.P.ValorTexto == "" ? "—" : i.P.ValorTexto + " (ilegível)"), i => i.P.ValorUsd),
        new("taxa", "Taxa aplicada", "right", i => i.P.ValorBrl is not null ? "já em R$" : i.Taxa is { } t ? t.ToString("0.0000", _pt) : "sem taxa", i => i.Taxa),
        new("brl", "Valor convertido (R$)", "right", i => i.Brl is { } b ? b.ToString("#,##0.00", _pt) : (!i.P.TemValor ? (i.P.ValorTexto == "" ? "—" : i.P.ValorTexto + " (ilegível)") : "Não convertido"), i => i.Brl),
        new("linha", "Linha na planilha", "center", i => i.P.Linha.ToString(), i => i.P.Linha),
    };

    // ---- ordem das colunas: arrasta o cabeçalho; a ordem fica guardada por usuário
    private const string TabelaOrdemPref = "conversao_base_ordem";
    private string[]? _ordemColunas;
    private string[] OrdemColunas => _ordemColunas ??= ColPrefs.Get(_login, TabelaOrdemPref)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Colunas na ordem do usuário; as que não estão na ordem gravada (coluna nova) ficam no fim.</summary>
    private List<Col> Colunas()
    {
        var cols = ColunasPadrao();
        var ordem = OrdemColunas;
        if (ordem.Length == 0) return cols;
        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordem.Length; i++) pos.TryAdd(ordem[i], i);
        return cols.Select((c, i) => (c, k: pos.TryGetValue(c.Key, out var p) ? p : ordem.Length + i))
                   .OrderBy(x => x.k).Select(x => x.c).ToList();
    }

    /// <summary>Nova ordem depois de arrastar um cabeçalho (chamado pelo JS).</summary>
    [JSInvokable]
    public Task SaveColumnOrder(string tabela, string[] ordem) => InvokeAsync(() =>
    {
        // a ordem que chega tem só as colunas visíveis; as ocultas mantêm o lugar relativo no fim
        var vis = (ordem ?? Array.Empty<string>()).ToList();
        var resto = Colunas().Select(c => c.Key).Where(k => !vis.Contains(k));
        _ordemColunas = vis.Concat(resto).ToArray();
        ColPrefs.Save(_login, TabelaOrdemPref, string.Join(",", _ordemColunas));
        StateHasChanged();
    });

    private void RestaurarOrdem()
    {
        ColPrefs.Reset(_login, TabelaOrdemPref);
        _ordemColunas = Array.Empty<string>();
    }

    // a escolha das colunas visíveis fica guardada por usuário (v2: colunas fixas, sem as originais da planilha)
    private void CarregarOcultas()
    {
        var pref = ColPrefs.Get(_login, TabelaPref);
        _ocultas = pref.StartsWith("v2:", StringComparison.Ordinal)
            ? new HashSet<string>(pref[3..].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal)
            : new HashSet<string>(new[] { "usd", "taxa", "linha" }, StringComparer.Ordinal);
    }
    private void AlternarColuna(string key)
    {
        _ocultas ??= new HashSet<string>(StringComparer.Ordinal);
        if (!_ocultas.Remove(key)) _ocultas.Add(key);
        ColPrefs.Save(_login, TabelaPref, "v2:" + string.Join(",", _ocultas));
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
        if (_classes.Count > 0) q = q.Where(i => _classes.Contains(ChaveClasse(i.P.Classe)));
        if (!string.IsNullOrWhiteSpace(_busca))
        {
            var b = _busca.Trim();
            // em inglês a busca olha o texto mostrado na tela (em português, o de sempre)
            q = q.Where(i => cols.Any(c => (_emIngles ? TextoCol(c, i) : c.Texto(i)).Contains(b, StringComparison.CurrentCultureIgnoreCase)));
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
                        case "usd": if (i.P.ValorUsd is { } u) { cel.Value = u; cel.Style.NumberFormat.Format = "#,##0.00"; } else if (i.P.ValorBrl is null && i.P.ValorTexto != "") cel.Value = i.P.ValorTexto + " (ilegível)"; break;
                        case "taxa": if (i.P.ValorBrl is not null) cel.Value = "já em R$"; else if (i.Taxa is { } t) { cel.Value = t; cel.Style.NumberFormat.Format = "0.0000"; } else cel.Value = "sem taxa"; break;
                        case "brl": if (i.Brl is { } b) { cel.Value = b; cel.Style.NumberFormat.Format = "#,##0.00"; } else if (i.P.TemValor) cel.Value = "Não convertido"; else if (i.P.ValorTexto != "") cel.Value = i.P.ValorTexto + " (ilegível)"; break;
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
        if (_ref is not null)
        {
            try { await JS.InvokeVoidAsync("cvLargura.parar"); } catch { }
            _ref.Dispose();
        }
    }

    // ======================================================================
    //  LARGURA DOS GRÁFICOS
    // ======================================================================
    // O viewBox dos gráficos de evolução e de valor mensal segue a largura real do cartão:
    // o texto sai com o tamanho escrito (12,5 px) a 1366 e a 1920 px, como nos gráficos do Power BI.
    // ---- modo embutido (guia Performance Comercial) ---------------------------------
    /// <summary>true = só as seções de BU, mercado, produto, país, perdas e base, sem cabeçalho nem barra de filtros.</summary>
    [Parameter] public bool Embutido { get; set; }
    /// <summary>Embutido: filtros compartilhados com a página de fora (a barra e as seções usam os mesmos).</summary>
    [Parameter] public FiltrosConversao? Filtros { get; set; }
    /// <summary>Embutido: "filtros" = só a barra de filtros (congelada); "secoes" = só as seções.</summary>
    [Parameter] public string Parte { get; set; } = "secoes";
    /// <summary>Embutido: avisa a página de fora quando algum filtro muda (para ela recalcular o que é dela).</summary>
    [Parameter] public EventCallback FiltrosMudaram { get; set; }
    /// <summary>Embutido: quantas linhas ficam visíveis nas listas (o resto rola).</summary>
    [Parameter] public int LinhasVisiveis { get; set; } = 7;
    private string? _assinNotif;

    // ---- base da taxa de conversão (botão na barra de filtros) --------------------------
    private bool _baseTodasLocal = true;   // padrão: taxa sobre todas as propostas enviadas
    /// <summary>true = Ganhas ÷ todas as propostas enviadas (padrão); false = Ganhas ÷ decididas. Embutido: vem dos filtros compartilhados.</summary>
    private bool _baseTodas
    {
        get => Filtros?.BaseTodas ?? _baseTodasLocal;
        set { if (Filtros is not null) Filtros.BaseTodas = value; else _baseTodasLocal = value; }
    }
    // ---- conversão por quantidade ou por valor (segundo botão da barra de filtros) ----
    private bool _porValorLocal;
    private bool _porValor
    {
        get => Filtros?.PorValor ?? _porValorLocal;
        set { if (Filtros is not null) Filtros.PorValor = value; else _porValorLocal = value; }
    }
    private string DenRot => _baseTodas ? T("enviadas", "sent") : T("decididas", "decided");
    private string RegraCurta => _porValor
        ? (_baseTodas ? T($"{Sim} ganho ÷ {Sim} de todas as enviadas", $"{Sim} won ÷ {Sim} of all sent") : T($"{Sim} ganho ÷ {Sim} (ganho + perdido)", $"{Sim} won ÷ {Sim} (won + lost)"))
        : (_baseTodas ? T("ganhas ÷ todas as propostas enviadas", "won ÷ all proposals sent") : T("ganhas ÷ decididas", "won ÷ decided"));
    private string TituloRegra => _porValor
        ? (_baseTodas ? T("Taxa de Conversão por valor = valor ganho ÷ valor de todas as propostas enviadas × 100.",
                          "Conversion rate by value = won value ÷ value of all proposals sent × 100.")
                      : T("Taxa de Conversão por valor = valor ganho ÷ (valor ganho + valor perdido) × 100. Abertas e não classificadas fora.",
                          "Conversion rate by value = won value ÷ (won value + lost value) × 100. Open and unclassified excluded."))
        : _baseTodas
        ? T("Taxa de Conversão = Ganhas ÷ todas as propostas enviadas × 100 (ganhas, perdidas, abertas e não classificadas).",
            "Conversion rate = Won ÷ all proposals sent × 100 (won, lost, open and unclassified).")
        : T("Taxa de Conversão = Ganhas ÷ (Ganhas + Perdidas) × 100. Propostas abertas e não classificadas não entram no denominador.",
            "Conversion rate = Won ÷ (Won + Lost) × 100. Open and unclassified proposals are not in the denominator.");
    private string TituloValor => _baseTodas
        ? T($"Valor ganho ÷ valor total cotado (todas as propostas), em {Sim}", $"Won value ÷ total quoted value (all proposals), in {Sim}")
        : T($"Valor ganho ÷ (valor ganho + valor perdido), em {Sim}", $"Won value ÷ (won value + lost value), in {Sim}");

    // ---- idioma da tela (botão "Idioma" da barra de filtros) ----
    private bool _emInglesLocal;
    /// <summary>true = textos em inglês e números no formato americano; false = português (padrão). Embutido: vem dos filtros compartilhados.</summary>
    private bool _emIngles
    {
        get => Filtros?.EmIngles ?? _emInglesLocal;
        set { if (Filtros is not null) Filtros.EmIngles = value; else _emInglesLocal = value; }
    }
    /// <summary>O texto no idioma da tela: T("Propostas enviadas", "Proposals sent").</summary>
    private string T(string pt, string en) => _emIngles ? en : pt;

    // ---- taxa da tela (ao lado de US$): só para converter o que a tela mostra, não grava nada ----
    private double? _taxaTelaLocal;
    private double? _taxaTela
    {
        get => Filtros is not null ? Filtros.TaxaTela : _taxaTelaLocal;
        set { if (Filtros is not null) Filtros.TaxaTela = value; else _taxaTelaLocal = value; }
    }
    /// <summary>A taxa da tela que vale agora (dólar ligado e taxa digitada); null = as taxas anuais.</summary>
    private double? _taxaTelaAtiva => _emDolar && _taxaTela is > 0 ? _taxaTela : null;
    /// <summary>Dólar pelas taxas anuais (sem taxa da tela): AFM no valor original, NB ÷ taxa do ano.</summary>
    private bool _dolarAnual => _emDolar && _taxaTelaAtiva is null;
    private string _taxaTelaTexto = "";
    private void MudarTaxaTela(ChangeEventArgs e)
    {
        var t = (e.Value?.ToString() ?? "").Trim().Replace(",", ".");
        _taxaTelaTexto = e.Value?.ToString() ?? "";
        _taxaTela = double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        if (_taxaTela is null) _taxaTelaTexto = "";
    }

    // ---- moeda dos valores (terceiro botão da barra de filtros) ----
    private bool _emDolarLocal;
    /// <summary>true = valores em US$; false = em R$ (padrão). Embutido: vem dos filtros compartilhados.</summary>
    private bool _emDolar
    {
        get => Filtros?.EmDolar ?? _emDolarLocal;
        set { if (Filtros is not null) Filtros.EmDolar = value; else _emDolarLocal = value; }
    }

    protected override void OnParametersSet()
    {
        if (Filtros is not { } f || ReferenceEquals(_anos, f.Anos)) return;
        _anos = f.Anos; _meses = f.Meses; _vend = f.Vendedores; _bus = f.Bus; _buIcs = f.BuIcs;
        _origens = f.Origens; _mercados = f.Mercados; _paises = f.Paises; _produtos = f.Produtos;
    }

    /// <summary>Embutido: os atalhos do índice apontam para as seções da guia Performance Comercial.</summary>
    private (string Id, string Titulo)[] AncorasEmbutido => new[]
    {
        ("propostas", T("Resumo", "Summary")), ("mes-a-mes", T("Mês a mês", "Month by month")), ("mensal", T("Ano × ano", "Year × year")), ("setores", "AFM × NB"), ("metas", T("Vendas × metas", "Sales × targets")), ("unidades", "BU"),
        ("mercado", T("Mercado", "Market")), ("produto", T("Produto", "Product")), ("vendedores", T("Vendedores", "Sales team")), ("pais", T("País", "Country")),
        ("perdas", T("Perdas", "Losses")), ("base", T("Base", "Data")), ("budget", T("Acumulado", "Year to date")), ("mes-ano", T("Mês × ano", "Month × year")),
    };

    private double _chartW = 1200;
    private bool _largObs;
    private DotNetObjectReference<Conversao>? _ref;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_temDados || _relatorio) return;
        _ref ??= DotNetObjectReference.Create(this);
        // cabeçalho arrastável da Base e mapa de países (cada chamada só liga o que ainda não está ligado)
        try { await JS.InvokeVoidAsync("colOrder.init", "cv-th-base", _ref, TabelaOrdemPref); } catch { }
        try { await JS.InvokeVoidAsync("worldMap.init", MapaId, _ref); } catch { }
        if (Embutido) { try { await JS.InvokeVoidAsync("vpLinhas", LinhasVisiveis); } catch { } }
        if (Embutido && Filtros is not null)
        {
            // filtro mudou aqui dentro (barra, clique numa lista, mapa): a página de fora recalcula
            var assin = Filtros.Assinatura();
            if (_assinNotif is null) _assinNotif = assin;
            else if (assin != _assinNotif) { _assinNotif = assin; await FiltrosMudaram.InvokeAsync(); }
        }
        if (_largObs) return;
        try
        {
            var w = await JS.InvokeAsync<double>("cvLargura.observar", _ref);
            if (w > 0) { _largObs = true; CvLargura(w); }
        }
        catch { }
    }

    // ---- mapa de países (mesmo componente da Visão Executiva) ----------------------
    private const string MapaId = "cv-worldmap";

    // faixa de taxas do recorte, para o mapa ter contraste mesmo quando os países ficam perto uns dos outros
    private double _txMin, _txMax = 100;

    /// <summary>
    /// Cor pela taxa de conversão: claro → azul-petróleo, como o mapa da Visão Executiva. A escala vai da
    /// menor à maior taxa entre os países do recorte (a lista ao lado mostra o número exato).
    /// </summary>
    private string CorTaxa(Resumo r)
    {
        if (!_taxaOk || r.TaxaQtd is not { } t) return "#c9d3de";
        var faixa = _txMax - _txMin;
        var k = faixa < 0.5 ? 0.75 : 0.22 + 0.78 * Math.Clamp((t - _txMin) / faixa, 0, 1);
        int R = (int)(226 + (11 - 226) * k), G = (int)(238 + (79 - 238) * k), B = (int)(245 + (111 - 245) * k);
        return $"rgb({R},{G},{B})";
    }

    /// <summary>Mapa-múndi (mesmos contornos da Visão Executiva) pintado pela taxa de conversão de cada país.</summary>
    private string SvgMapaPaises(List<(string Nome, Resumo R)> lista)
    {
        // mais de um rótulo da planilha pode cair no mesmo país do mapa ("Brasil" e "Brazil"): soma
        var porChave = new Dictionary<string, (Resumo R, List<string> Nomes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nome, _) in lista)
        {
            if (WorldMap.Chave(nome) is not { } k) continue;
            if (!porChave.TryGetValue(k, out var e)) porChave[k] = e = (new Resumo { Todas = _baseTodas, PorValor = _porValor }, new List<string>());
            e.Nomes.Add(nome);
        }
        var resumos = porChave.ToDictionary(kv => kv.Key, kv => (R: Somar(_fx.Where(i => kv.Value.Nomes.Contains(i.P.Pais))), kv.Value.Nomes));
        var taxas = lista.Select(x => x.R.TaxaQtd).Where(x => x is not null).Select(x => x!.Value).ToList();
        (_txMin, _txMax) = taxas.Count == 0 ? (0d, 100d) : (taxas.Min(), taxas.Max());

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox='0 0 {F(WorldMap.ViewW)} {F(WorldMap.ViewH)}' width='100%' preserveAspectRatio='xMidYMid meet' role='img' aria-label='{T("Taxa de conversão por país", "Conversion rate by country")}'>");
        sb.Append($"<rect x='0' y='0' width='{F(WorldMap.ViewW)}' height='{F(WorldMap.ViewH)}' fill='#f7fafc'/>");
        foreach (var (pais, d) in WorldMap.Paths)
        {
            var tem = resumos.TryGetValue(pais, out var e);
            var ativo = tem && e.Nomes.Any(_paises.Contains);
            var fill = tem ? CorTaxa(e.R) : "#e7edf3";
            var dados = tem
                ? $" data-xf-val='{Esc(e.Nomes[0])}' data-value='{Esc(T("Taxa de conversão ", "Conversion rate ") + TxQtd(e.R))}' data-pct='{Esc(T($"{N0(e.R.G)} ganhas de {N0(e.R.Decididas)} decididas", $"{N0(e.R.G)} won of {N0(e.R.Decididas)} decided"))}' data-metric='{Esc(T($"{N0(e.R.Total)} propostas", $"{N0(e.R.Total)} proposals"))}'/>"
                : "/>";
            sb.Append($"<path d='{d}' fill='{fill}' stroke='{(ativo ? "#0b4f6f" : "#cdd8e4")}' stroke-width='{(ativo ? "1.2" : "0.4")}' class='wm-path{(ativo ? " wm-active" : "")}' data-country='{Esc(pais)}'{dados}");
        }
        foreach (var (pais, e) in resumos)
        {
            if (!WorldMap.Centroids.TryGetValue(pais, out var pos) || !_taxaOk || e.R.TaxaQtd is not { } t) continue;
            var escuro = t >= 50;
            sb.Append($"<text x='{F(pos.X)}' y='{F(pos.Y + 3)}' text-anchor='middle' font-size='9' font-weight='700' fill='{(escuro ? "#fff" : "#1e2836")}' pointer-events='none'>{Esc(Math.Round(t).ToString(Inv))}%</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Clique num país do mapa (JS): filtra o painel por ele.</summary>
    [JSInvokable]
    public Task ApplyCrossFilter(string dim, string val) => InvokeAsync(() =>
    {
        if (dim == "country" && !string.IsNullOrWhiteSpace(val)) Alternar(_paises, val);
        StateHasChanged();
    });

    /// <summary>Duplo clique no oceano: tira o filtro de país.</summary>
    [JSInvokable]
    public Task ClearAllFilters() => InvokeAsync(() => { _paises.Clear(); StateHasChanged(); });

    [JSInvokable]
    public void CvLargura(double w)
    {
        var n = Math.Round(Math.Clamp(w, 640, 1800));
        if (Math.Abs(n - _chartW) < 1) return;
        _chartW = n;
        StateHasChanged();
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
        foreach (var a in _taxasAnos) _taxasTxt[a] = salvas.TryGetValue(a, out var t) ? t.ToString("0.0000", CulTela) : "";
        _taxasAberto = true;
    }

    private void AdicionarAno()
    {
        if (int.TryParse(_novoAno.Trim(), out var a) && a is >= 2000 and <= 2100)
        {
            if (!_taxasAnos.Contains(a)) { _taxasAnos.Add(a); _taxasAnos.Sort(); _taxasTxt[a] = ""; }
            _novoAno = ""; _taxasErro = "";
        }
        else _taxasErro = T("Informe um ano válido (ex.: 2027).", "Enter a valid year (e.g. 2027).");
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
            if (v is { } x && (double.IsNaN(x) || x <= 0 || x >= 100)) { _taxasErro = T($"Taxa inválida em {a}. Use um número entre 0 e 100 (ex.: 5,2000).", $"Invalid rate for {a}. Use a number between 0 and 100 (e.g. 5.2000)."); return; }
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
        _f.Where(i => i.P.ValorBrl is null).GroupBy(AnoTxt).OrderBy(g => g.Key)   // só AFM: o NB já vem em R$
          .Select(g => (Ano: g.Key,
                        Taxa: g.Select(i => i.Taxa).FirstOrDefault(t => t is not null),
                        Propostas: g.Count(),
                        Usd: g.Sum(i => i.P.ValorUsd ?? 0),
                        Brl: g.Sum(i => i.Brl ?? 0),
                        SemTaxa: g.Count(i => i.P.ValorUsd is not null && i.Brl is null))).ToList();
}
