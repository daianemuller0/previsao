using System.Globalization;
using System.Security.Claims;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// APRESENTAÇÃO EXECUTIVA | GLOBAL COMMERCIAL PERFORMANCE — o conteúdo.
//
// Monta os slides a partir dos MESMOS cálculos do Resumo Executivo
// (ResumoExecutivoCalc.Montar), sobre uma cópia única dos dados tirada no
// momento da geração (FonteApresentacao): pré-visualização e arquivo saem do
// mesmo retrato. Conclusões são determinísticas: FATO → EVIDÊNCIA → IMPLICAÇÃO
// → AÇÃO, sem inventar causas, responsáveis ou motivos de perda.
//
// Segurança: só dados agregados (nada de cliente, número de proposta ou nome de
// vendedor); permissão conferida aqui, no servidor (Negado), e não só na página.
// ---------------------------------------------------------------------------

public enum PeriodoApresentacao { Mes, Ytd, Ano, Historico }

public sealed class OpcoesApresentacao
{
    public int AnoRef { get; set; } = DateTime.Today.Year;
    public int MesRef { get; set; } = Math.Max(1, DateTime.Today.Month - 1);
    public PeriodoApresentacao Periodo { get; set; } = PeriodoApresentacao.Ytd;
    public bool Ingles { get; set; } = true;
    /// <summary>consolidado | setor | bu | pais | segmento | produto</summary>
    public string Escopo { get; set; } = "consolidado";
    public string? ValorEscopo { get; set; }
    public bool Usd { get; set; }
    public int MaxSlides { get; set; } = 22;
    public bool Detalhadas { get; set; } = true;
    public bool Apendices { get; set; } = true;
    public bool CompararAnoAnterior { get; set; } = true;
    public bool BaseTodas { get; set; } = true;
    public int AnoInicial { get; set; } = 2022;

    public string Assinatura() => string.Join("|", AnoRef, MesRef, Periodo, Ingles, Escopo, ValorEscopo, Usd, MaxSlides, Detalhadas, Apendices, CompararAnoAnterior, BaseTodas, AnoInicial);
}

/// <summary>A cópia dos dados usada numa geração (uma versão só, do começo ao fim).</summary>
public sealed record FonteApresentacao(IReadOnlyList<PropostaConv> Propostas, IReadOnlyDictionary<int, double> Taxas,
    IReadOnlyList<MesDeVendas> Vendas, IReadOnlyList<BudgetDoAno> Budgets, DateTime? PropostasLidasEm, DateTime Capturado);

public sealed class ResultadoApresentacao
{
    public required AxDeck Deck { get; init; }
    public required string Arquivo { get; init; }
    public List<string> Omitidos { get; } = new();
    public List<string> Avisos { get; } = new();
}

public static class GeradorApresentacao
{
    public static readonly (string Id, string Pt, string En)[] Escopos =
    {
        ("consolidado", "Consolidado", "Consolidated"), ("setor", "Setor (AFM / NB)", "Sector (AFM / NB)"), ("bu", "Business Unit", "Business Unit"),
        ("pais", "País", "Country"), ("segmento", "Segmento", "Segment"), ("produto", "Produto", "Product"),
    };

    /// <summary>
    /// Controle de acesso no servidor. Administrador: qualquer recorte (inclusive o consolidado global).
    /// Gestor Aftermarket: só o setor AFM. Demais perfis: não geram. null = permitido.
    /// </summary>
    public static string? Negado(ClaimsPrincipal? u, OpcoesApresentacao o)
    {
        if (u?.Identity?.IsAuthenticated != true) return "Acesso não autenticado.";
        if (u.IsInRole(AccessRoles.Admin)) return null;
        var papel = AccessRoles.Normalize(u.FindFirst(ClaimTypes.Role)?.Value);
        if (papel == AccessRoles.GestorAfm)
            return o.Escopo == "setor" && string.Equals(o.ValorEscopo, ConversaoService.Afm, StringComparison.OrdinalIgnoreCase)
                ? null : "Seu perfil só pode gerar a apresentação do setor AFM.";
        return "Seu perfil não tem permissão para gerar a Apresentação Executiva.";
    }

    public static ResultadoApresentacao Montar(ClaimsPrincipal? u, FonteApresentacao fonte, OpcoesApresentacao o, string autor)
    {
        if (Negado(u, o) is string motivo) throw new UnauthorizedAccessException(motivo);
        if (o.Escopo != "consolidado" && string.IsNullOrWhiteSpace(o.ValorEscopo)) throw new ArgumentException("Escolha o valor do recorte.");
        return new ConstrutorApresentacao(fonte, o, autor).Montar();
    }

    /// <summary>O .pptx a partir do desenho já montado (o mesmo da pré-visualização).</summary>
    public static byte[] Pptx(ClaimsPrincipal? u, OpcoesApresentacao o, ResultadoApresentacao r)
    {
        if (Negado(u, o) is string motivo) throw new UnauthorizedAccessException(motivo);
        return AxPptx.Gerar(r.Deck, Modelo());
    }

    /// <summary>O modelo corporativo (embutido no programa; não fica em pasta pública).</summary>
    public static byte[] Modelo()
    {
        using var s = typeof(GeradorApresentacao).Assembly.GetManifestResourceStream("ModeloApresentacaoExecutiva.pptx")
            ?? throw new InvalidOperationException("Modelo da apresentação não encontrado no programa.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>As imagens de fundo do modelo (capa, páginas internas, encerramento) para a pré-visualização.</summary>
    public static (string Capa, string Interna, string Encerramento) Fundos()
    {
        using var z = new System.IO.Compression.ZipArchive(new MemoryStream(Modelo()));
        string Img(string n)
        {
            using var s = z.GetEntry("ppt/media/" + n)!.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        return (Img("image1.png"), Img("image2.png"), Img("image3.png"));
    }
}

internal sealed class ConstrutorApresentacao
{
    // ---------------- estado ----------------
    private readonly FonteApresentacao _fonte;
    private readonly OpcoesApresentacao _o;
    private readonly string _autor;
    private readonly bool _en;
    private readonly CultureInfo _ci;
    private readonly ModeloResumo _m;
    private readonly int R, C, Mr, De, Ate, AteV, UltV;
    private readonly bool Equiv, Escopado, Cmp;
    private readonly List<int> Anos;
    private readonly Dictionary<string, int> _inicio;
    private readonly int _anoComparavel;
    private List<int>? _anosP;
    /// <summary>Anos com propostas de todas as origens do recorte (sem anos vazios como zero nem anos com só uma origem).</summary>
    private List<int> AnosP => _anosP ??= Anos.Where(a => a >= _anoComparavel && !IA(a).Vazio).DefaultIfEmpty(R).ToList();
    private bool SemPropComparavel(int a) => a < _anoComparavel || IA(a).Vazio;
    private readonly DateTime Corte;
    private readonly List<(string Id, string Titulo, string Motivo)> _omitidos = new();
    private readonly string _rotEscopo;

    private static readonly string[] MesesEn = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] MesesPt = { "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez" };

    public ConstrutorApresentacao(FonteApresentacao fonte, OpcoesApresentacao o, string autor)
    {
        _fonte = fonte; _o = o; _autor = autor;
        _en = o.Ingles;
        _ci = new CultureInfo(_en ? "en-US" : "pt-BR");
        R = o.AnoRef; C = R - 1; Mr = Math.Clamp(o.MesRef, 1, 12);
        Corte = new DateTime(R, Mr, DateTime.DaysInMonth(R, Mr));
        De = o.Periodo == PeriodoApresentacao.Mes ? Mr : 1; Ate = Mr;
        Equiv = o.Periodo is PeriodoApresentacao.Mes or PeriodoApresentacao.Ytd;
        Escopado = o.Escopo != "consolidado";
        Cmp = o.CompararAnoAnterior;
        var ini = o.Periodo == PeriodoApresentacao.Ano ? R - 1 : Math.Min(o.AnoInicial, R - 1);
        Anos = Enumerable.Range(ini, R - ini + 1).ToList();

        var f = new FiltroResumo { AnoDe = ini, AnoAte = R, BaseTodas = o.BaseTodas };
        var v = o.ValorEscopo ?? "";
        switch (o.Escopo)
        {
            case "setor": f.Setores.Add(v); break;
            case "bu": f.Bus.Add(v); break;
            case "pais": f.Paises.Add(v); break;
            case "segmento": f.Segmentos.Add(v); break;
            case "produto": f.Produtos.Add(v); break;
        }
        _rotEscopo = Escopado ? $"{NomeEscopo(o.Escopo)}: {v}" : T("Consolidated (all sectors, BUs and countries)", "Consolidado (todos os setores, BUs e países)");

        // corte: nada depois do último dia do mês de referência
        var props = fonte.Propostas.Where(p => p.Data is DateTime d && d.Date <= Corte).ToList();
        var vendas = fonte.Vendas.Where(x => x.Ano < R || (x.Ano == R && x.Mes <= Mr)).ToList();
        _m = ResumoExecutivoCalc.Montar(props, fonte.Taxas, vendas, fonte.Budgets, f, R, C, true, (R, Mr));
        // AFM e NB podem começar em anos diferentes (NB tem a planilha histórica): os gráficos de propostas
        // começam no primeiro ano em que TODAS as origens do recorte têm dados, para comparar igual com igual.
        _inicio = props.Where(p => f.Passa(p)).GroupBy(p => p.Origem).ToDictionary(g => g.Key, g => g.Min(p => p.Data!.Value.Year));
        _anoComparavel = _inicio.Count == 0 ? R : _inicio.Values.Max();
        UltV = _m.UltimoMesVendas(R);
        AteV = Math.Min(Ate, UltV);
    }

    // ---------------- idioma e formatos ----------------
    private string T(string en, string pt) => _en ? en : pt;
    private string Mn(int m) => (_en ? MesesEn : MesesPt)[m - 1];
    private string Faixa(int de, int ate) => de == ate ? Mn(de) : $"{Mn(de)}–{Mn(ate)}";
    private string NomeEscopo(string id)
    {
        var e = GeradorApresentacao.Escopos.FirstOrDefault(x => x.Id == id);
        return e.Id is null ? id : _en ? e.En : e.Pt;
    }
    private string Data(DateTime d, bool hora = false) => _en ? d.ToString(hora ? "dd MMM yyyy HH:mm" : "dd MMM yyyy", _ci) : d.ToString(hora ? "dd/MM/yyyy HH:mm" : "dd/MM/yyyy", _ci);
    private string Sim => _o.Usd ? "US$" : "R$";
    private string NA => T("n/a", "n/d");
    private string Indisp => T("Data unavailable", "Dados indisponíveis");

    private string Compacto(decimal a) =>
        a >= 1_000_000_000m ? (a / 1_000_000_000m).ToString("#,##0.00", _ci) + T("B", " bi")
        : a >= 1_000_000m ? (a / 1_000_000m).ToString("#,##0.0", _ci) + T("M", " mi")
        : a >= 1_000m ? (a / 1_000m).ToString("#,##0", _ci) + T("k", " mil") : a.ToString("#,##0", _ci);
    private string Din(decimal? v) => v is not decimal x ? NA : (x < 0 ? "−" : "") + Sim + " " + Compacto(Math.Abs(x));
    private string Din(double v) => Din((decimal)v);
    private string Pct(double? v) => v is double x ? x.ToString("0.0", _ci) + "%" : NA;
    private string Pp(double? v) => v is double x ? (x >= 0.05 ? "+" : x <= -0.05 ? "−" : "±") + Math.Abs(x).ToString("0.0", _ci) + " p.p." : NA;
    private string Var(double? v) => v is double x ? (x >= 0.05 ? "+" : x <= -0.05 ? "−" : "±") + Math.Abs(x).ToString("0.0", _ci) + "%" : NA;
    private string N(int v) => v.ToString("#,##0", _ci);
    private string N1(double v) => v.ToString("#,##0.0", _ci);
    private static double? VarPct(decimal? a, decimal? b) => a is decimal x && b is decimal y && y > 0 ? (double)((x - y) / y * 100) : null;
    private static double? VarPct(double a, double b) => b > 0 ? (a - b) / b * 100 : null;
    private static double? Dif(double? a, double? b) => a is double x && b is double y ? x - y : null;

    /// <summary>Converte R$ do ano para a moeda escolhida (US$ pela taxa anual cadastrada).</summary>
    private decimal? Mo(decimal? brl, int ano) =>
        brl is not decimal v ? null : !_o.Usd ? v : _fonte.Taxas.TryGetValue(ano, out var t) && t > 0 ? v / (decimal)t : null;
    private decimal? Mo(double brl, int ano) => Mo((decimal)brl, ano);
    private double? MoMi(decimal? brl, int ano) => Mo(brl, ano) is decimal v ? (double)(v / 1_000_000m) : null;
    private double? MoMi(double brl, int ano) => MoMi((decimal)brl, ano);
    private string FmtMi => _en ? $"\"{Sim} \"#,##0.0\"M\"" : $"\"{Sim} \"#,##0.0\" mi\"";
    private string FmtMil => _en ? $"\"{Sim} \"#,##0\"k\"" : $"\"{Sim} \"#,##0\" mil\"";
    private const string FmtPct = "0.0\"%\"", FmtQtd = "#,##0";

    // ---------------- recortes de tempo ----------------
    private Indicadores Ind(int ano, int de, int ate) => _m.Soma(_m.Mes.Where(kv => kv.Key.Ano == ano && kv.Key.Mes >= de && kv.Key.Mes <= ate).Select(kv => kv.Value));
    private decimal? Ven(int ano, int de, int ate)
    {
        if (ate < de) return null;
        var l = _m.VendasMes.Where(kv => kv.Key.Ano == ano && kv.Key.Mes >= de && kv.Key.Mes <= ate).ToList();
        return l.Count == 0 ? null : l.Sum(kv => kv.Value);
    }
    /// <summary>Indicadores do período do relatório (mês, YTD…) num ano.</summary>
    private Indicadores IP(int ano) => Ind(ano, De, Ate);
    /// <summary>Vendas do período (só os meses com venda registrada no ano de referência, nos dois anos).</summary>
    private decimal? VP(int ano) => Ven(ano, De, AteV);
    /// <summary>Base dos gráficos anuais: meses equivalentes (jan–corte) ou ano inteiro.</summary>
    private int AteA => Equiv ? Mr : 12;
    private Indicadores IA(int ano) => Ind(ano, 1, AteA);
    private decimal? VA(int ano) => Ven(ano, 1, Equiv ? AteV : 12);
    private Indicadores DimP(string dim, string k, int ano) =>
        _m.Soma(_m.Dim[dim].Celulas.Where(kv => kv.Key.Chave == k && kv.Key.Ano == ano && kv.Key.Mes >= De && kv.Key.Mes <= Ate).Select(kv => kv.Value));

    private string PerR => _o.Periodo switch
    {
        PeriodoApresentacao.Mes => $"{Mn(Mr)} {R}",
        PeriodoApresentacao.Ytd => Mr == 12 ? $"{R}" : T($"YTD {Mn(Mr)} {R}", $"{Faixa(1, Mr)} {R} (acumulado)"),
        _ => Mr == 12 ? $"{R}" : $"{R} ({Faixa(1, Mr)})",
    };
    private string PerC => _o.Periodo == PeriodoApresentacao.Mes ? $"{Mn(Mr)} {C}" : Mr == 12 ? $"{C}" : $"{Faixa(1, Mr)} {C}";
    private string PerVendasR => AteV <= 0 ? Indisp : _o.Periodo == PeriodoApresentacao.Mes ? $"{Mn(AteV)} {R}" : $"{Faixa(1, AteV)} {R}";
    private string BaseAnualTxt => Equiv
        ? (Mr == 12 ? T("full calendar years", "anos inteiros") : T($"{Faixa(1, Mr)} of each year (equivalent period)", $"{Faixa(1, Mr)} de cada ano (período equivalente)"))
        : (Mr == 12 ? T("full calendar years", "anos inteiros") : T($"full years; {R} up to {Mn(Mr)} (year to date)", $"anos inteiros; {R} até {Mn(Mr)} (acumulado)"));
    private string RotAno(int a) => !Equiv && a == R && Mr < 12 ? T($"{a} YTD", $"{a} acum.") : a.ToString();
    private string Regra => _o.BaseTodas
        ? T("Conversion = won ÷ all proposals sent (value: R$ won ÷ R$ sent).", "Conversão = ganhas ÷ todas as enviadas (valor: R$ ganho ÷ R$ enviado).")
        : T("Conversion = won ÷ (won + lost); open proposals excluded (value: R$ won ÷ R$ won + lost).", "Conversão = ganhas ÷ (ganhas + perdidas); abertas fora (valor: R$ ganho ÷ R$ ganho + perdido).");
    private string Fonte => T(
        "Sources: proposals = AFM and NB spreadsheets (send date; each proposal number counted once); sales = team sales history (company-wide, billing month); targets = annual HSA/Group budgets.",
        "Fontes: propostas = planilhas AFM e NB (data de envio; cada número de proposta uma vez); vendas = histórico da equipe (empresa toda, mês de faturamento); metas = budgets anuais HSA/Grupo.");

    // ---------------- cores ----------------
    private string CorAno(int a) => a == R ? AxCores.Verde : a == C ? AxCores.Azul : (R - a) switch { 2 => "8FA39D", 3 => "B0BEB9", _ => "CBD5D1" };
    private string CorBarraAno(int a) => a == R ? AxCores.Verde : a == C ? AxCores.VerdeMedio : (R - a) switch { 2 => "6FA597", 3 => "97BDB3", _ => "B9D3CC" };

    // ================= MONTAGEM =================
    public ResultadoApresentacao Montar()
    {
        var slides = new List<(AxSlide S, int Prioridade)>();
        void Add(AxSlide? s, int prioridade) { if (s is not null) slides.Add((s, prioridade)); }

        Add(Capa(), 0);
        Add(SumarioExecutivo(), 0);
        Add(VendasAnuais(), 1);
        Add(VendasMensais(), 2);
        Add(VendasXEsforco(), 7);
        Add(Eficiencia(), 8);
        Add(Conversao(), 1);
        Add(Pipeline(), 2);
        Add(Perdas(), 6);
        Add(Metas(), 1);
        if (_o.Escopo != "setor") Add(AfmNb(), 4); else Omitir("afmnb", T("AFM vs NB", "AFM × NB"), T("the deck is already filtered by sector", "a apresentação já é de um setor"));
        if (_o.Detalhadas)
        {
            Add(Dimensao("bu", T("Business Units", "Business Units"), T("Where is the value and the opportunity?", "Onde está o valor e a oportunidade?")), 5);
            Add(Dimensao("pais", T("Countries", "Países"), T("Where do we win?", "Onde ganhamos?")), 9);
            Add(Dimensao("segmento", T("Segments", "Segmentos"), T("Which markets convert best?", "Quais mercados convertem mais?")), 9);
            Add(Dimensao("produto", T("Products", "Produtos"), T("Which product mix sells?", "Qual mix de produtos vende?")), 10);
            Add(Capacidade(), 8);
        }
        else Omitir("detalhes", T("Detailed analyses (BU, country, segment, product, capacity)", "Análises detalhadas (BU, país, segmento, produto, capacidade)"), T("not selected in the generation options", "não selecionadas nas opções"));
        Add(Oportunidades(), 3);
        Add(Cenarios(), 4);
        Add(PlanoDeAcao(), 3);
        Add(Conclusoes(), 0);
        AxSlide? defs = _o.Apendices ? Definicoes() : null;
        if (defs is not null) Add(defs, 6);
        var encerramento = new AxSlide { Tipo = AxTipoSlide.Encerramento, Id = "fim", Titulo = T("Closing", "Encerramento"), Notas = T("Closing slide (corporate template).", "Slide de encerramento (modelo corporativo).") };

        // limite de slides: sai primeiro o que tem menor prioridade (maior número); capa, sumário e conclusões ficam sempre
        var limite = Math.Max(8, _o.MaxSlides) - 1 - (_o.Apendices ? 1 : 0);   // encerramento e limitações
        while (slides.Count > limite)
        {
            var sai = slides.Where(x => x.Prioridade > 0).OrderByDescending(x => x.Prioridade).ThenByDescending(x => slides.IndexOf(x)).FirstOrDefault();
            if (sai.S is null) break;
            slides.Remove(sai);
            Omitir(sai.S.Id, sai.S.Titulo, T($"slide limit ({_o.MaxSlides})", $"limite de slides ({_o.MaxSlides})"));
        }
        var lista = slides.Select(x => x.S).ToList();
        if (_o.Apendices) lista.Add(Limitacoes());
        lista.Add(encerramento);

        // numeração e rodapé nos slides internos
        for (var i = 0; i < lista.Count; i++)
            if (lista[i].Tipo == AxTipoSlide.Conteudo)
            {
                lista[i].Elems.Add(Txt(10881360, 246888, 694944, 210312, new AxRun((i + 1).ToString("00"), 1000, true, AxCores.Texto), "r"));
                lista[i].Elems.Add(Txt(521207, 6446520, 9144000, 192024, new AxRun(Rodape, 800, false, AxCores.Cinza)));
            }

        var deck = new AxDeck
        {
            Titulo = $"Global Commercial Performance – {PerR}",
            Assunto = T("Howden South America | Sales Performance & Commercial Efficiency (confidential)", "Howden South America | Performance de Vendas e Eficiência Comercial (confidencial)"),
            Autor = _autor, Idioma = _en ? "en-US" : "pt-BR", Gerado = _fonte.Capturado,
        };
        deck.Slides.AddRange(lista);
        var r = new ResultadoApresentacao { Deck = deck, Arquivo = NomeArquivo() };
        r.Omitidos.AddRange(_omitidos.Select(o => $"{o.Titulo}: {o.Motivo}"));
        if (UltV < Mr && !Escopado) r.Avisos.Add(T($"Sales for {R} are recorded up to {(UltV > 0 ? Mn(UltV) : "—")}: sales comparisons use {Faixa(1, Math.Max(1, UltV))}.",
                                                    $"As vendas de {R} estão lançadas até {(UltV > 0 ? Mn(UltV) : "—")}: as comparações de vendas usam {Faixa(1, Math.Max(1, UltV))}."));
        if (_o.Usd && Anos.Any(a => !_fonte.Taxas.ContainsKey(a))) r.Avisos.Add(T("No USD exchange rate for: ", "Sem taxa de câmbio para: ") + string.Join(", ", Anos.Where(a => !_fonte.Taxas.ContainsKey(a))));
        return r;
    }

    private string NomeArquivo()
    {
        var n = $"Global_Commercial_Performance_{R}_{Mr:00}";
        if (_o.Periodo != PeriodoApresentacao.Ytd) n += "_" + _o.Periodo switch { PeriodoApresentacao.Mes => "Month", PeriodoApresentacao.Ano => "Year", _ => "History" };
        if (Escopado)
        {
            var v = new string((_o.ValorEscopo ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
            n += "_" + (v.Length > 0 ? v[..Math.Min(30, v.Length)] : _o.Escopo);
        }
        if (_o.Usd) n += "_USD";
        if (!_en) n += "_PT";
        return n + ".pptx";
    }

    private string Rodape => T(
        $"CONFIDENTIAL  •  Howden Sales Forecast  •  cut-off {Data(Corte)}  •  {_rotEscopo}  •  aggregated data, no client or proposal identifiers",
        $"CONFIDENCIAL  •  Howden Sales Forecast  •  corte {Data(Corte)}  •  {_rotEscopo}  •  dados agregados, sem clientes nem números de proposta");

    private void Omitir(string id, string titulo, string motivo) => _omitidos.Add((id, titulo, motivo));

    // ================= peças de layout (padrão do modelo) =================
    private const long XL = 585216, XR = 11576304, Y0 = 1563624, YB = 5532120;

    private static AxForma Txt(long x, long y, long w, long h, AxRun r, string alinh = "l", string ancora = "t") =>
        new(x, y, w, h) { Pars = new[] { new AxPar(r, alinh, 0) }, Ancora = ancora };

    private AxSlide Novo(string id, string titulo, string subtitulo, string kicker = "GLOBAL COMMERCIAL PERFORMANCE")
    {
        var s = new AxSlide { Id = id, Titulo = titulo };
        s.Elems.Add(Txt(502920, 246888, 9601200, 210312, new AxRun(kicker, 1000, true, AxCores.Verde)));
        var tam = AxMedida.Caber(titulo, 11109960, 438912, 2600, 1800, 1.1);
        s.Elems.Add(new AxForma(502920, 557784, 11109960, 438912) { Titulo = true, Pars = new[] { new AxPar(new AxRun(titulo, tam, true, AxCores.Escuro), "l", 0) } });
        s.Elems.Add(Txt(530352, 1024128, 10972800, 320040, new AxRun(subtitulo, AxMedida.Caber(subtitulo, 10972800, 320040, 1200, 900), false, AxCores.Texto)));
        s.Elems.Add(new AxLinha(502920, 1353312, 11183112, AxCores.Verde));
        return s;
    }

    private void Barra(AxSlide s, string rotulo, string texto)
    {
        s.Elems.Add(new AxForma(566928, 5687568, 11009376, 502920) { Geo = "roundRect", Fundo = AxCores.Barra });
        s.Elems.Add(Txt(749808, 5687568, 2468880, 502920, new AxRun(rotulo, 1100, true, AxCores.Escuro), "l", "ctr"));
        s.Elems.Add(Txt(3291840, 5705856, 8138160, 466344, new AxRun(texto, AxMedida.Caber(texto, 8138160, 466344, 1100, 800), false, AxCores.Escuro), "l", "ctr"));
    }

    private void Grafico(AxSlide s, long x, long y, long w, long h, string titulo, AxChart ch, string? nota = null)
    {
        s.Elems.Add(new AxForma(x, y, w, h) { Geo = "roundRect", Arredondado = 5000, Fundo = AxCores.Claro, Borda = AxCores.Linha });
        s.Elems.Add(Txt(x + 128016, y + 82296, w - 256032, 228600, new AxRun(titulo, AxMedida.Caber(titulo, w - 256032, 228600, 1100, 850), true, AxCores.Escuro)));
        var notaH = nota is null ? 64008L : 256032L;
        s.Elems.Add(new AxGrafico(x + 54864, y + 329184, w - 109728, h - 329184 - notaH, ch));
        if (nota is not null) s.Elems.Add(Txt(x + 128016, y + h - 228600, w - 256032, 192024, new AxRun(nota, AxMedida.Caber(nota, w - 256032, 192024, 800, 650), false, AxCores.Texto, true)));
    }

    /// <summary>Cartão da direita do modelo: pergunta do gráfico + FATO / IMPLICAÇÃO / AÇÃO (fonte ajustada para caber).</summary>
    private void Cartao(AxSlide s, long x, long y, long w, long h, string pergunta, params (string Rot, string Txt)[] secoes)
    {
        s.Elems.Add(new AxForma(x, y, w, h) { Geo = "roundRect", Fundo = AxCores.CardDir, Borda = AxCores.CardDirBorda });
        var wi = w - 320040; var hi = h - 256032;
        int tq = 1400, tb = 1100;
        double Altura(int q, int b) =>
            (AxMedida.Linhas(pergunta, wi, q) * q * 1.22 + 900 * 1.22 + 400 +
             secoes.Sum(sx => 900 * 1.22 + 100 + AxMedida.Linhas(sx.Txt, wi, b) * b * 1.22 + 450)) / 100.0 * AxMedida.Pt;
        while (Altura(tq, tb) > hi && tb > 750) { tb -= 50; if (tq > 1150) tq -= 50; }
        var pars = new List<AxPar>
        {
            new(new AxRun(T("KEY QUESTION", "PERGUNTA-CHAVE"), 900, true, AxCores.VerdeMedio), "l", 100),
            new(new AxRun(pergunta, tq, true, AxCores.Escuro), "l", 500),
        };
        foreach (var (rot, txt) in secoes)
        {
            pars.Add(new AxPar(new AxRun(rot, 900, true, AxCores.VerdeMedio), "l", 100));
            pars.Add(new AxPar(new AxRun(txt, tb, false, AxCores.Texto), "l", 450));
        }
        s.Elems.Add(new AxForma(x + 160020, y + 128016, wi, hi) { Pars = pars });
    }

    private void Kpi(AxSlide s, long x, long y, long w, long h, string rot, string valor, string sub, string acento)
    {
        s.Elems.Add(new AxForma(x, y, w, h) { Geo = "roundRect", Arredondado = 8000, Fundo = AxCores.Card, Borda = AxCores.Linha });
        s.Elems.Add(new AxForma(x, y + 45720, 50292, h - 91440) { Fundo = acento });
        s.Elems.Add(Txt(x + 146304, y + 100584, w - 228600, 192024, new AxRun(rot, AxMedida.Caber(rot, w - 228600, 192024, 1000, 800), true, AxCores.Texto)));
        s.Elems.Add(Txt(x + 137160, y + 320040, w - 228600, 384048, new AxRun(valor, AxMedida.Caber(valor, w - 228600, 384048, 2300, 1300, 1.0), true, AxCores.Escuro)));
        s.Elems.Add(Txt(x + 146304, y + h - 320040, w - 228600, 274320, new AxRun(sub, AxMedida.Caber(sub, w - 228600, 274320, 900, 700), false, AxCores.Texto)));
    }

    private void LinhaNumerada(AxSlide s, long y, long h, string num, string titulo, string texto)
    {
        s.Elems.Add(new AxForma(640080, y, 10881360, h) { Geo = "roundRect", Fundo = AxCores.CardLinha, Borda = "E0ECE7" });
        s.Elems.Add(Txt(804672, y, 502920, h, new AxRun(num, 1900, true, AxCores.Verde), "l", "ctr"));
        s.Elems.Add(Txt(1371600, y, 2926080, h, new AxRun(titulo, AxMedida.Caber(titulo, 2926080, h, 1400, 1000), true, AxCores.Escuro), "l", "ctr"));
        s.Elems.Add(Txt(4389120, y + 45720, 6949440, h - 91440, new AxRun(texto, AxMedida.Caber(texto, 6949440, h - 91440, 1150, 750), false, AxCores.Texto), "l", "ctr"));
    }

    private static AxCelula Cab(string t, string alinh = "ctr") => new(t, true, "FFFFFF", null, alinh);
    private AxCelula Num(string t, string? cor = null, bool negrito = false, string? fundo = null) => new(t, negrito, cor ?? AxCores.Escuro, fundo, "r");
    private static AxCelula Rot(string t, bool negrito = false, string? fundo = null) => new(t, negrito, AxCores.Escuro, fundo, "l");
    private static string CorVar(double? v, bool neutra = false) => v is not double x || neutra || Math.Abs(x) < 0.05 ? AxCores.Texto : x > 0 ? AxCores.VerdeMedio : AxCores.Vermelho;
    private static string Curto(string s, int max = 30) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private string Notas(string objetivo, string fato, string evidencia, string implicacao, string acao, string? extra = null) =>
        string.Join("\n", new[]
        {
            T("PURPOSE: ", "OBJETIVO: ") + objetivo,
            T("FACT: ", "FATO: ") + fato,
            T("EVIDENCE: ", "EVIDÊNCIA: ") + evidencia,
            T("IMPLICATION: ", "IMPLICAÇÃO: ") + implicacao,
            T("ACTION: ", "AÇÃO: ") + acao,
            extra is null ? "" : extra,
            T($"PERIOD: {PerR} (comparison: {PerC}); cut-off {Data(Corte)}; scope: {_rotEscopo}.", $"PERÍODO: {PerR} (comparação: {PerC}); corte {Data(Corte)}; recorte: {_rotEscopo}."),
            Regra, Fonte,
            T("Proposal status is the current status at data extraction; send date ≠ decision date ≠ sales recognition (billing month).",
              "O status das propostas é o atual, na extração dos dados; data de envio ≠ data de decisão ≠ reconhecimento da venda (mês de faturamento)."),
            T("Confidential – aggregated data only.", "Confidencial – somente dados agregados."),
        }.Where(l => l.Length > 0));

    // ================= SLIDES =================

    private AxSlide Capa()
    {
        var s = new AxSlide { Tipo = AxTipoSlide.Capa, Id = "capa", Titulo = "Global Commercial Performance" };
        s.Elems.Add(Txt(530352, 841248, 6858000, 347472, new AxRun(T("MONTHLY EXECUTIVE REVIEW", "REVISÃO EXECUTIVA MENSAL"), 1700, true, AxCores.Verde)));
        s.Elems.Add(Txt(530352, 1453896, 6858000, 548640, new AxRun("Global Commercial Performance", 3200, true, AxCores.Escuro)));
        s.Elems.Add(new AxForma(530352, 2112264, 6583680, 740664)
        {
            Pars = new[] { new AxPar(new AxRun(T("Howden South America | Sales Performance & Commercial Efficiency", "Howden South America | Performance de Vendas e Eficiência Comercial"), 1600, false, AxCores.Texto), "l", 0) },
        });
        var lidas = _fonte.PropostasLidasEm is DateTime q ? q : _fonte.Capturado;
        var linhas = new[]
        {
            (T("Period", "Período"), PerR + (Cmp ? T($" · compared with {PerC}", $" · comparado com {PerC}") : "")),
            (T("Reference year", "Ano de referência"), R.ToString()),
            (T("Data updated", "Dados atualizados"), T($"{Data(lidas, true)} · cut-off {Data(Corte)}", $"{Data(lidas, true)} · corte {Data(Corte)}")),
            (T("Scope", "Recorte"), _rotEscopo),
            (T("Currency", "Moeda"), _o.Usd ? T("USD (US$), converted at the annual rate registered in the app", "USD (US$), convertido pela taxa anual cadastrada") : "BRL (R$)"),
        };
        s.Elems.Add(new AxForma(530352, 3017520, 6400800, 1645920)
        {
            Pars = linhas.Select(l => new AxPar(new[] { new AxRun(l.Item1 + ": ", 1200, true, AxCores.Escuro), new AxRun(l.Item2, 1200, false, AxCores.Texto) }, "l", 300)).ToList(),
        });
        s.Elems.Add(Txt(530352, 5806440, 6858000, 301752, new AxRun(T("CONFIDENTIAL • AGGREGATED DATA • INTERNAL USE ONLY", "CONFIDENCIAL • DADOS AGREGADOS • USO INTERNO"), 1100, true, AxCores.VerdeMedio)));
        s.Notas = T("Cover. ", "Capa. ") + string.Join(" | ", linhas.Select(l => l.Item1 + ": " + l.Item2)) + "\n" +
                  T($"Generated by {_autor} on {Data(_fonte.Capturado, true)} from a single data snapshot. Confidential.", $"Gerado por {_autor} em {Data(_fonte.Capturado, true)} a partir de um único retrato dos dados. Confidencial.");
        return s;
    }

    private AxSlide SumarioExecutivo()
    {
        var ip = IP(R); var ic = IP(C);
        var vr = Mo(VP(R), R); var vc = Mo(VP(C), C);
        var s = Novo("sumario", T("Executive Summary | Results and conversion", "Executive Summary | Resultado e conversão"),
            T($"{PerR}{(Cmp ? $" vs {PerC}" : "")} · {_rotEscopo}", $"{PerR}{(Cmp ? $" × {PerC}" : "")} · {_rotEscopo}"));
        long x0 = XL, w = 2624328, gap = 155448, y = 1636776, h = 1033272;
        string vsTxt(double? v, bool pp = false) => Cmp ? T($"vs {PerC}: ", $"vs {PerC}: ") + (pp ? Pp(v) : Var(v)) : "";
        var metaAno = _m.Metas.TryGetValue(R, out var mt) ? Mo(mt.Hsa, R) : null;
        var vendasAno = Mo(Ven(R, 1, UltV), R);
        if (!Escopado)
        {
            Kpi(s, x0, y, w, h, T("SALES", "VENDAS") + $" · {PerVendasR}", Din(vr), vsTxt(VarPct(vr, vc)), AxCores.VerdeMedio);
        }
        else Kpi(s, x0, y, w, h, T("WON VALUE", "VALOR GANHO") + $" · {PerR}", Din(Mo(ip.VG, R)), vsTxt(VarPct(Mo(ip.VG, R), Mo(ic.VG, C))), AxCores.VerdeMedio);
        Kpi(s, x0 + (w + gap), y, w, h, T("QUOTED VALUE", "VALOR OFERTADO"), Din(Mo(ip.ValorEnviado, R)),
            T($"{N(ip.Enviadas)} proposals", $"{N(ip.Enviadas)} propostas") + (Cmp ? $" · {Var(VarPct(ip.Enviadas, (double)ic.Enviadas))}" : ""), AxCores.Azul);
        Kpi(s, x0 + 2 * (w + gap), y, w, h, T("CONVERSION (QTY)", "CONVERSÃO (QTD)"), Pct(ip.TaxaQtd),
            (Cmp ? Pp(Dif(ip.TaxaQtd, ic.TaxaQtd)) + " · " : "") + T("value ", "valor ") + Pct(ip.TaxaValor), AxCores.VerdeMedio);
        if (!Escopado && metaAno is decimal ma && ma > 0 && vendasAno is decimal va)
            Kpi(s, x0 + 3 * (w + gap), y, w, h, T("HSA TARGET ATTAINMENT", "ATINGIMENTO META HSA"), Pct((double)(va / ma * 100)),
                T($"{Din(va)} of {Din(ma)} (annual)", $"{Din(va)} de {Din(ma)} (anual)"), AxCores.Azul);
        else
            Kpi(s, x0 + 3 * (w + gap), y, w, h, T("OPEN PIPELINE", "PIPELINE ABERTO"), Din(Mo(ip.VA, R)), T($"{N(ip.A)} open proposals sent in {PerR}", $"{N(ip.A)} abertas enviadas em {PerR}"), AxCores.Azul);

        var f1 = !Escopado && vr is not null
            ? T($"Sales of {Din(vr)} in {PerVendasR}", $"Vendas de {Din(vr)} em {PerVendasR}") + (Cmp && vc is not null ? T($", {Var(VarPct(vr, vc))} vs {Din(vc)} in the same months of {C}.", $", {Var(VarPct(vr, vc))} contra {Din(vc)} nos mesmos meses de {C}.") : ".")
              + (metaAno is decimal m1 && m1 > 0 && vendasAno is decimal v1 ? T($" {Pct((double)(v1 / m1 * 100))} of the annual HSA target.", $" {Pct((double)(v1 / m1 * 100))} da meta HSA anual.") : "")
            : T($"Won value of {Din(Mo(ip.VG, R))} in {PerR}", $"Valor ganho de {Din(Mo(ip.VG, R))} em {PerR}") + (Cmp ? $" ({Var(VarPct(Mo(ip.VG, R), Mo(ic.VG, C)))} vs {PerC})." : ".")
              + (Escopado ? T(" Sales history is company-wide only.", " O histórico de vendas é só da empresa toda.") : "");
        var f2 = T($"Conversion {Pct(ip.TaxaQtd)} by quantity and {Pct(ip.TaxaValor)} by value", $"Conversão de {Pct(ip.TaxaQtd)} por quantidade e {Pct(ip.TaxaValor)} por valor")
                 + (Cmp ? T($" ({Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))} and {Pp(Dif(ip.TaxaValor, ic.TaxaValor))} vs {C})", $" ({Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))} e {Pp(Dif(ip.TaxaValor, ic.TaxaValor))} vs {C})") : "")
                 + T($"; {N(ip.Enviadas)} proposals sent, {Din(Mo(ip.ValorEnviado, R))} quoted.", $"; {N(ip.Enviadas)} propostas enviadas, {Din(Mo(ip.ValorEnviado, R))} ofertados.");
        var f3 = T($"{Din(Mo(ip.VA, R))} still open in {N(ip.A)} proposals sent in {PerR}; {Din(Mo(ip.VP, R))} lost in {N(ip.P)} proposals ({N(ip.Canceladas)} cancelled).",
                   $"{Din(Mo(ip.VA, R))} ainda em aberto em {N(ip.A)} propostas enviadas em {PerR}; {Din(Mo(ip.VP, R))} perdidos em {N(ip.P)} propostas ({N(ip.Canceladas)} canceladas).");
        LinhaNumerada(s, 2862072, 777240, "01", T("RESULT", "RESULTADO"), f1);
        LinhaNumerada(s, 3758184, 777240, "02", T("EFFICIENCY", "EFICIÊNCIA"), f2);
        LinhaNumerada(s, 4654296, 777240, "03", T("VALUE AT STAKE", "VALOR EM JOGO"), f3);

        var subiuV = vr is not null && vc is not null && vr >= vc; var subiuC = Dif(ip.TaxaQtd, ic.TaxaQtd) is >= 0;
        var msg = !Escopado && vr is not null && vc is not null && Cmp
            ? T($"Sales {(subiuV ? "up" : "down")} {Var(VarPct(vr, vc))} with conversion {(subiuC ? "up" : "down")} {Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))}: priority is converting the {Din(Mo(ip.VA, R))} open pipeline.",
                $"Vendas {(subiuV ? "em alta" : "em queda")} ({Var(VarPct(vr, vc))}) e conversão {(subiuC ? "em alta" : "em queda")} ({Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))}): prioridade é converter os {Din(Mo(ip.VA, R))} em aberto.")
            : T($"Priority: convert the {Din(Mo(ip.VA, R))} open pipeline and review the {Din(Mo(ip.VP, R))} lost.", $"Prioridade: converter os {Din(Mo(ip.VA, R))} em aberto e revisar os {Din(Mo(ip.VP, R))} perdidos.");
        Barra(s, T("EXECUTIVE MESSAGE", "MENSAGEM EXECUTIVA"), msg);
        s.Notas = Notas(T("Summarise result, efficiency and value at stake.", "Resumir resultado, eficiência e valor em jogo."), f1, f2, f3, msg,
            T("Sales are company-wide (billing month); proposals by send month; conversion by the cohort's current status.", "Vendas da empresa toda (mês de faturamento); propostas pelo mês de envio; conversão pelo status atual da coorte."));
        return s;
    }

    private AxSlide? VendasAnuais()
    {
        if (Escopado) { Omitir("vendas_anuais", T("Annual Sales", "Vendas anuais"), T("sales history is only recorded company-wide (not by BU, country, sector, segment or product)", "o histórico de vendas só existe para a empresa toda (não por BU, país, setor, segmento ou produto)")); return null; }
        var vals = Anos.Select(a => MoMi(VA(a), a)).ToArray();
        if (vals.All(v => v is null)) { Omitir("vendas_anuais", T("Annual Sales", "Vendas anuais"), Indisp); return null; }
        var s = Novo("vendas_anuais", T("Annual Sales | How much did we sell?", "Vendas anuais | Quanto vendemos?"), $"{Anos.First()}–{R} · {BaseAnualTxt}");
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = Anos.Select(RotAno).ToArray(), Formato = FmtMi, Legenda = false, Espaco = 70,
            Series = { new AxSerie(T("Sales", "Vendas"), vals, AxCores.VerdeMedio) { CoresPontos = Anos.Select(a => a == R ? AxCores.Verde : null).ToArray() } },
            Descricao = T("Sales by year", "Vendas por ano"),
        };
        Grafico(s, XL, Y0, 7424928, YB - Y0, T($"Sales by year ({Sim} millions)", $"Vendas por ano ({Sim} milhões)"), ch, T($"Basis: {BaseAnualTxt}. Sales = company-wide history by billing month.", $"Base: {BaseAnualTxt}. Vendas = histórico da empresa por mês de faturamento."));
        var vr = Mo(VP(R), R); var vc = Mo(VP(C), C);
        var comDados = Anos.Select(a => (a, v: Mo(VA(a), a))).Where(x => x.v is not null && !(x.a == R && !Equiv && Mr < 12)).ToList();
        var melhor = comDados.Count > 0 ? comDados.MaxBy(x => x.v!.Value) : default;
        var pior = comDados.Count > 0 ? comDados.MinBy(x => x.v!.Value) : default;
        var fato = T($"{Din(vr)} in {PerVendasR}", $"{Din(vr)} em {PerVendasR}") + (Cmp ? T($" vs {Din(vc)} in the same months of {C} ({Var(VarPct(vr, vc))}).", $" contra {Din(vc)} nos mesmos meses de {C} ({Var(VarPct(vr, vc))}).") : ".");
        var evid = comDados.Count >= 2 ? T($"Highest: {melhor.a} ({Din(melhor.v)}); lowest: {pior.a} ({Din(pior.v)}), same basis.", $"Maior: {melhor.a} ({Din(melhor.v)}); menor: {pior.a} ({Din(pior.v)}), mesma base.") : Indisp;
        var impl = vr is not null && vc is not null && vr >= vc
            ? T("The year is ahead of the prior year on a like-for-like basis.", "O ano está à frente do anterior na mesma base de meses.")
            : T("The year is behind the prior year on a like-for-like basis.", "O ano está atrás do anterior na mesma base de meses.");
        var acao = T("Track the monthly run-rate against the annual target (see Target Achievement).", "Acompanhar o ritmo mensal contra a meta anual (ver Atingimento de metas).");
        Cartao(s, 8247888, Y0, 3328416, YB - Y0, T("How much did we sell per year, and is the year ahead of the last one?", "Quanto vendemos por ano, e o ano está à frente do anterior?"),
            (T("FACT", "FATO"), fato), (T("EVIDENCE", "EVIDÊNCIA"), evid), (T("IMPLICATION", "IMPLICAÇÃO"), impl), (T("ACTION", "AÇÃO"), acao));
        var yoy = Anos.Skip(1).Select(a => (a, v: VarPct(Ven(a, 1, Equiv ? AteV : (a == R ? AteV : 12)), Ven(a - 1, 1, Equiv || a == R ? AteV : 12)))).Where(x => x.v is not null).ToList();
        Barra(s, T("YEAR-ON-YEAR", "ANO CONTRA ANO"), yoy.Count == 0 ? Indisp : string.Join("  ·  ", yoy.Select(x => $"{RotAno(x.a)} {Var(x.v)}")) + T(" (same months)", " (mesmos meses)"));
        s.Notas = Notas(T("Show annual sales history.", "Mostrar o histórico anual de vendas."), fato, evid, impl, acao,
            T("Growth is always computed on the same months of each year (equivalent period).", "O crescimento é sempre calculado nos mesmos meses de cada ano (período equivalente)."));
        return s;
    }

    private AxSlide? VendasMensais()
    {
        if (Escopado) { Omitir("vendas_mensais", T("Monthly Sales", "Vendas mensais"), T("sales history is only recorded company-wide", "o histórico de vendas só existe para a empresa toda")); return null; }
        var anos = Anos.TakeLast(5).ToList();
        if (!_m.VendasMes.Any()) { Omitir("vendas_mensais", T("Monthly Sales", "Vendas mensais"), Indisp); return null; }
        var s = Novo("vendas_mensais", T("Monthly Sales | Where are the peaks and dips?", "Vendas mensais | Onde estão os picos e as quedas?"),
            T($"{anos.First()}–{R} by billing month · {R} up to {(UltV > 0 ? Mn(UltV) : "—")} · negative months shown as recorded", $"{anos.First()}–{R} por mês de faturamento · {R} até {(UltV > 0 ? Mn(UltV) : "—")} · meses negativos como lançados"));
        var series = anos.Select(a => new AxSerie(a.ToString(), Enumerable.Range(1, 12).Select(mes => _m.VendasMes.TryGetValue((a, mes), out var v) ? MoMi(v, a) : null).ToArray(), CorAno(a))
        {
            Rotulos = a == R, Espessura = a == R ? 31750 : a == C ? 22225 : 15875,
        }).ToList();
        var ch = new AxChart { Tipo = AxTipoGrafico.Linhas, Categorias = Enumerable.Range(1, 12).Select(Mn).ToArray(), Formato = "#,##0.0", Series = series, EixoValores = true, Descricao = T("Monthly sales by year", "Vendas mensais por ano") };
        Grafico(s, XL, Y0, XR - XL, 2468880, T($"Monthly sales ({Sim} millions) — labels on {R}", $"Vendas mensais ({Sim} milhões) — rótulos em {R}"), ch);
        // tabela compacta: anos × meses
        var linhas = new List<AxCelula[]> { new[] { Cab(T("Year", "Ano"), "l") }.Concat(Enumerable.Range(1, 12).Select(mm => Cab(Mn(mm), "r"))).Append(Cab(T("Total", "Total"), "r")).ToArray() };
        foreach (var a in anos)
        {
            var cel = new List<AxCelula> { Rot(a.ToString(), true, a == R ? "E6F2EB" : null) };
            for (var mes = 1; mes <= 12; mes++)
            {
                var futuro = a == R && mes > UltV;
                if (_m.VendasMes.TryGetValue((a, mes), out var v) && MoMi(v, a) is double d)
                    cel.Add(Num(d.ToString("#,##0.0", _ci), d < 0 ? AxCores.Vermelho : null, false, a == R ? "E6F2EB" : null));
                else cel.Add(Num(futuro ? "·" : "–", AxCores.Cinza, false, a == R ? "E6F2EB" : null));
            }
            var tot = Enumerable.Range(1, 12).Select(mes => _m.VendasMes.TryGetValue((a, mes), out var v) ? MoMi(v, a) : null).Where(x => x is not null).Sum(x => x!.Value);
            cel.Add(Num(tot.ToString("#,##0.0", _ci) + (a == R && UltV < 12 ? "*" : ""), null, true, a == R ? "E6F2EB" : null));
            linhas.Add(cel.ToArray());
        }
        var hTab = 228600L * linhas.Count;
        s.Elems.Add(new AxTabela(XL, Y0 + 2560320, XR - XL, hTab) { Pesos = new[] { 1.1 }.Concat(Enumerable.Repeat(1.0, 12)).Append(1.3).ToArray(), Linhas = linhas, Tam = 800, Nome = "Monthly sales table" });
        var meses = _m.VendasMes.Where(kv => anos.Contains(kv.Key.Ano)).ToList();
        var mx = meses.MaxBy(kv => kv.Value); var mn = meses.MinBy(kv => kv.Value);
        var neg = meses.Count(kv => kv.Value < 0);
        var txt = T($"Best month: {Mn(mx.Key.Mes)}/{mx.Key.Ano} ({Din(Mo(mx.Value, mx.Key.Ano))}); weakest: {Mn(mn.Key.Mes)}/{mn.Key.Ano} ({Din(Mo(mn.Value, mn.Key.Ano))})",
                    $"Melhor mês: {Mn(mx.Key.Mes)}/{mx.Key.Ano} ({Din(Mo(mx.Value, mx.Key.Ano))}); mais fraco: {Mn(mn.Key.Mes)}/{mn.Key.Ano} ({Din(Mo(mn.Value, mn.Key.Ano))})")
                  + (neg > 0 ? T($"; {neg} month(s) with negative sales (adjustments/reversals as recorded).", $"; {neg} mês(es) com venda negativa (ajustes/estornos como lançados).") : ".")
                  + T(" * year to date · = month not closed.", " * acumulado · = mês não fechado.");
        Barra(s, T("READING", "LEITURA"), txt);
        s.Notas = Notas(T("Show seasonality and monthly volatility.", "Mostrar sazonalidade e volatilidade mensal."), txt,
            T("Table under the chart gives every monthly value (millions).", "A tabela sob o gráfico traz todos os valores mensais (milhões)."),
            T("Single months are volatile; read with the year-to-date trend.", "Meses isolados oscilam; ler junto com o acumulado."),
            T("Investigate negative or atypical months in the sales history before drawing conclusions.", "Verificar meses negativos ou atípicos no histórico antes de concluir."));
        return s;
    }

    private AxSlide? VendasXEsforco()
    {
        if (Escopado) { Omitir("vendas_esforco", T("Sales vs Quotation Effort", "Vendas × esforço comercial"), T("sales history is only recorded company-wide", "o histórico de vendas só existe para a empresa toda")); return null; }
        if (Anos.All(a => IA(a).Enviadas == 0)) { Omitir("vendas_esforco", T("Sales vs Quotation Effort", "Vendas × esforço comercial"), Indisp); return null; }
        var s = Novo("vendas_esforco", T("Sales vs Quotation Effort | How much did we have to quote?", "Vendas × esforço | Quanto precisamos ofertar?"), $"{Anos.First()}–{R} · {BaseAnualTxt}");
        long w = (XR - XL - 182880) / 2;
        var vendas = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = Anos.Select(RotAno).ToArray(), Formato = FmtMi, Legenda = false,
            Series = { new AxSerie(T("Sales", "Vendas"), Anos.Select(a => MoMi(VA(a), a)).ToArray(), AxCores.VerdeMedio) { CoresPontos = Anos.Select(a => a == R ? AxCores.Verde : null).ToArray() } },
            Descricao = T("Sales by year", "Vendas por ano"),
        };
        var esforco = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = Anos.Select(RotAno).ToArray(), Formato = FmtMi,
            Series =
            {
                new AxSerie(T("Quoted value", "Valor ofertado"), Anos.Select(a => SemPropComparavel(a) ? null : MoMi(IA(a).ValorEnviado, a)).ToArray(), AxCores.Azul),
                new AxSerie(T("Proposals sent", "Propostas enviadas"), Anos.Select(a => !SemPropComparavel(a) && IA(a).Enviadas > 0 ? (double?)IA(a).Enviadas : null).ToArray(), AxCores.Laranja) { Linha = true, Formato = FmtQtd },
            },
            Descricao = T("Quoted value and proposals by year", "Valor ofertado e propostas por ano"),
        };
        Grafico(s, XL, Y0, w, YB - Y0 - 640080, T($"Sales ({Sim} millions)", $"Vendas ({Sim} milhões)"), vendas);
        var semProp = Anos.Where(SemPropComparavel).ToList();
        Grafico(s, XL + w + 182880, Y0, w, YB - Y0 - 640080, T($"Quotation effort: quoted value ({Sim} millions) and proposals", $"Esforço: valor ofertado ({Sim} milhões) e propostas"), esforco,
            semProp.Count == 0 ? null : T($"No comparable proposal data for {string.Join(", ", semProp)} (see limitations).", $"Sem dados comparáveis de propostas para {string.Join(", ", semProp)} (ver limitações)."));
        var ip = IA(R); var ic = IA(C);
        var nota = T("Correlation is not causation: proposals sent in a period do not necessarily convert into sales in the same period (sales are recognised at billing).",
                     "Correlação não é causalidade: propostas enviadas num período não viram, necessariamente, vendas no mesmo período (a venda é reconhecida no faturamento).");
        s.Elems.Add(new AxForma(XL, YB - 548640, XR - XL, 502920) { Pars = new[] { new AxPar(new AxRun(nota, 1000, false, AxCores.Texto, true), "l", 0) }, Ancora = "ctr" });
        var vr = Mo(VA(R), R); var vc = Mo(VA(C), C);
        var vpp = vr is decimal vx && ip.Enviadas > 0 ? vx / ip.Enviadas : (decimal?)null;
        var txt = T($"{RotAno(R)} vs {C} (same basis): sales {Var(VarPct(vr, vc))} · quoted value {Var(VarPct(Mo(ip.ValorEnviado, R), Mo(ic.ValorEnviado, C)))} · proposals {Var(VarPct(ip.Enviadas, (double)ic.Enviadas))} · sales per proposal {Din(vpp)}.",
                    $"{RotAno(R)} × {C} (mesma base): vendas {Var(VarPct(vr, vc))} · valor ofertado {Var(VarPct(Mo(ip.ValorEnviado, R), Mo(ic.ValorEnviado, C)))} · propostas {Var(VarPct(ip.Enviadas, (double)ic.Enviadas))} · venda por proposta {Din(vpp)}.");
        Barra(s, T("READING", "LEITURA"), txt);
        s.Notas = Notas(T("Compare the sales result with the quotation effort, side by side and on the same basis.", "Comparar o resultado de vendas com o esforço de oferta, lado a lado e na mesma base."), txt,
            T("Two separate charts with the same years; no causal link is implied.", "Dois gráficos separados com os mesmos anos; nenhum vínculo causal é sugerido."),
            T("More quotation does not automatically mean more sales; read with conversion.", "Mais oferta não significa automaticamente mais venda; ler junto com a conversão."),
            T("Review whether quoting effort is focused on the segments that convert (see Segments).", "Avaliar se o esforço de oferta está nos segmentos que convertem (ver Segmentos)."), nota);
        return s;
    }

    private AxSlide? Eficiencia()
    {
        if (AnosP.All(a => IA(a).Enviadas == 0)) { Omitir("eficiencia", T("Commercial Efficiency", "Eficiência comercial"), Indisp); return null; }
        var s = Novo("eficiencia", T("Commercial Efficiency | What does each proposal deliver?", "Eficiência comercial | Quanto rende cada proposta?"), $"{AnosP.First()}–{R} · {BaseAnualTxt}");
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = AnosP.Select(RotAno).ToArray(), Formato = FmtMil,
            Series =
            {
                new AxSerie(T("Avg. quoted value per proposal", "Valor médio ofertado"), AnosP.Select(a => Mo(IA(a).ValorMedio, a) is decimal v ? (double?)((double)v / 1000) : null).ToArray(), AxCores.Azul),
                new AxSerie(T("Avg. won proposal value", "Valor médio das ganhas"), AnosP.Select(a => Mo(IA(a).TicketGanhas, a) is decimal v ? (double?)((double)v / 1000) : null).ToArray(), AxCores.VerdeMedio),
            },
            Descricao = T("Average values per proposal", "Valores médios por proposta"),
        };
        long wg = 5303520;
        Grafico(s, XL, Y0, wg, YB - Y0, T($"Average value per proposal ({Sim} thousands)", $"Valor médio por proposta ({Sim} mil)"), ch);
        var linhas = new List<AxCelula[]> { new[] { Cab(T("Indicator", "Indicador"), "l") }.Concat(AnosP.Select(a => Cab(RotAno(a), "r"))).ToArray() };
        void L(string nome, Func<int, string> f, bool destaque = false) => linhas.Add(new[] { Rot(nome, destaque) }.Concat(AnosP.Select(a => Num(f(a), null, a == R, a == R ? "E6F2EB" : null))).ToArray());
        L(T("Proposals sent", "Propostas enviadas"), a => IA(a).Enviadas > 0 ? N(IA(a).Enviadas) : NA);
        L(T("Quoted value", "Valor ofertado"), a => IA(a).Enviadas > 0 ? Din(Mo(IA(a).ValorEnviado, a)) : NA);
        if (!Escopado) L(T("Sales per proposal sent", "Venda por proposta enviada"), a => VA(a) is decimal v && IA(a).Enviadas > 0 ? Din(Mo(v / IA(a).Enviadas, a)) : NA);
        L(T("Won value per proposal sent", "Valor ganho por proposta enviada"), a => IA(a).Enviadas > 0 ? Din(Mo((decimal)IA(a).VG / IA(a).Enviadas, a)) : NA);
        L(T("Conversion (qty)", "Conversão (qtd)"), a => Pct(IA(a).TaxaQtd), true);
        L(T("Conversion (value)", "Conversão (valor)"), a => Pct(IA(a).TaxaValor), true);
        var xt = XL + wg + 182880;
        s.Elems.Add(new AxTabela(xt, Y0, XR - xt, 320040L * linhas.Count) { Pesos = new[] { 2.3 }.Concat(AnosP.Select(_ => 1.0)).ToArray(), Linhas = linhas, Tam = AnosP.Count > 4 ? 850 : 950, Nome = "Efficiency table" });
        var ip = IA(R); var ic = IA(C);
        var txt = T($"Each proposal sent in {RotAno(R)} generated {Din(ip.Enviadas > 0 ? Mo((decimal)ip.VG / ip.Enviadas, R) : null)} in won value vs {Din(ic.Enviadas > 0 ? Mo((decimal)ic.VG / ic.Enviadas, C) : null)} in {C}; average quoted value {Din(Mo(ip.ValorMedio, R))} ({Var(VarPct(Mo(ip.ValorMedio, R), Mo(ic.ValorMedio, C)))}).",
                    $"Cada proposta enviada em {RotAno(R)} gerou {Din(ip.Enviadas > 0 ? Mo((decimal)ip.VG / ip.Enviadas, R) : null)} em valor ganho, contra {Din(ic.Enviadas > 0 ? Mo((decimal)ic.VG / ic.Enviadas, C) : null)} em {C}; valor médio ofertado {Din(Mo(ip.ValorMedio, R))} ({Var(VarPct(Mo(ip.ValorMedio, R), Mo(ic.ValorMedio, C)))}).");
        Barra(s, T("READING", "LEITURA"), txt);
        s.Notas = Notas(T("Show how much value each proposal delivers.", "Mostrar quanto valor cada proposta entrega."), txt,
            T("Averages computed from the yearly totals in the table.", "Médias calculadas dos totais anuais da tabela."),
            T("Recent cohorts still have open proposals: won value per proposal tends to rise as they are decided.", "Coortes recentes ainda têm abertas: o valor ganho por proposta tende a subir conforme forem decididas."),
            T("Prioritise proposals with higher expected value and conversion.", "Priorizar propostas de maior valor esperado e conversão."));
        return s;
    }

    private AxSlide? Conversao()
    {
        if (AnosP.All(a => IA(a).Linhas == 0)) { Omitir("conversao", T("Conversion Performance", "Conversão"), Indisp); return null; }
        var s = Novo("conversao", T("Conversion Performance | How much of what we quote do we win?", "Conversão | Quanto do que ofertamos ganhamos?"), $"{AnosP.First()}–{R} · {BaseAnualTxt} · " + (_o.BaseTodas ? T("base: all proposals", "base: todas as propostas") : T("base: decided proposals", "base: propostas decididas")));
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = AnosP.Select(RotAno).ToArray(), Formato = FmtPct, Min = 0,
            Series =
            {
                new AxSerie(T("By quantity", "Por quantidade"), AnosP.Select(a => IA(a).TaxaQtd).ToArray(), AxCores.VerdeMedio),
                new AxSerie(T("By value", "Por valor"), AnosP.Select(a => IA(a).TaxaValor).ToArray(), AxCores.Azul),
            },
            Descricao = T("Conversion rate by year", "Taxa de conversão por ano"),
        };
        Grafico(s, XL, Y0, 7424928, YB - Y0, T("Conversion rate by year (%)", "Taxa de conversão por ano (%)"), ch, Regra);
        var ip = IP(R); var ic = IP(C);
        long x = 8247888, w = 3328416, h = 1170432, g = 128016;
        Kpi(s, x, Y0, w, h, T($"QUANTITY · {PerR}", $"QUANTIDADE · {PerR}"), Pct(ip.TaxaQtd), Cmp ? T($"{Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))} vs {PerC} ({Pct(ic.TaxaQtd)})", $"{Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))} vs {PerC} ({Pct(ic.TaxaQtd)})") : T($"{N(ip.G)} won of {N(_o.BaseTodas ? ip.Linhas : ip.Decididas)}", $"{N(ip.G)} ganhas de {N(_o.BaseTodas ? ip.Linhas : ip.Decididas)}"), AxCores.VerdeMedio);
        Kpi(s, x, Y0 + h + g, w, h, T($"VALUE · {PerR}", $"VALOR · {PerR}"), Pct(ip.TaxaValor), Cmp ? T($"{Pp(Dif(ip.TaxaValor, ic.TaxaValor))} vs {PerC} ({Pct(ic.TaxaValor)})", $"{Pp(Dif(ip.TaxaValor, ic.TaxaValor))} vs {PerC} ({Pct(ic.TaxaValor)})") : Din(Mo(ip.VG, R)), AxCores.Azul);
        var abertas = ip.Linhas > 0 ? ip.A * 100.0 / ip.Linhas : (double?)null;
        Kpi(s, x, Y0 + 2 * (h + g), w, YB - Y0 - 2 * (h + g), T("STILL OPEN (SHARE OF SENT)", "AINDA EM ABERTO (DAS ENVIADAS)"), Pct(abertas),
            T($"{N(ip.A)} of {N(ip.Linhas)} proposals of {PerR}: the rate can still change", $"{N(ip.A)} de {N(ip.Linhas)} propostas de {PerR}: a taxa ainda pode mudar"), AxCores.Laranja);
        var txt = T($"{PerR}: {N(ip.G)} won, {N(ip.P)} lost, {N(ip.A)} open. Quantity {Pct(ip.TaxaQtd)}" + (Cmp ? $" ({Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))})" : "") + $", value {Pct(ip.TaxaValor)}" + (Cmp ? $" ({Pp(Dif(ip.TaxaValor, ic.TaxaValor))})." : "."),
                    $"{PerR}: {N(ip.G)} ganhas, {N(ip.P)} perdidas, {N(ip.A)} abertas. Quantidade {Pct(ip.TaxaQtd)}" + (Cmp ? $" ({Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))})" : "") + $", valor {Pct(ip.TaxaValor)}" + (Cmp ? $" ({Pp(Dif(ip.TaxaValor, ic.TaxaValor))})." : "."));
        Barra(s, T("DEFINITION", "DEFINIÇÃO"), txt);
        s.Notas = Notas(T("Show conversion by quantity and by value, with the official definition.", "Mostrar a conversão por quantidade e por valor, com a definição oficial."), txt,
            T("Rates use the proposal rows' current status (cohort by send date).", "Taxas pelo status atual das linhas (coorte pela data de envio)."),
            T("Quantity and value can diverge when large proposals are won or lost.", "Quantidade e valor divergem quando propostas grandes são ganhas ou perdidas."),
            T("Use the value rate to prioritise large opportunities; follow up open proposals.", "Usar a taxa por valor para priorizar grandes oportunidades; acompanhar as abertas."));
        return s;
    }

    private AxSlide? Pipeline()
    {
        if (AnosP.All(a => IA(a).Linhas == 0)) { Omitir("pipeline", "Pipeline", Indisp); return null; }
        var s = Novo("pipeline", T("Pipeline | Where is the quoted value today?", "Pipeline | Onde está o valor ofertado hoje?"),
            T($"Proposals by send year and current status (as of extraction) · {BaseAnualTxt}", $"Propostas por ano de envio e status atual (na extração) · {BaseAnualTxt}"));
        AxChart Pilha(bool valor) => new()
        {
            Tipo = AxTipoGrafico.ColunasEmpilhadas, Categorias = AnosP.Select(RotAno).ToArray(), Formato = valor ? "#,##0.0" : FmtQtd, Espaco = 55,
            Series =
            {
                new AxSerie(T("Won", "Ganhas"), AnosP.Select(a => valor ? MoMi(IA(a).VG, a) : IA(a).G).Select(v => v is > 0 ? v : null).ToArray(), AxCores.VerdeMedio),
                new AxSerie(T("Lost", "Perdidas"), AnosP.Select(a => valor ? MoMi(IA(a).VP, a) : IA(a).P).Select(v => v is > 0 ? v : null).ToArray(), AxCores.Vermelho),
                new AxSerie(T("Open", "Abertas"), AnosP.Select(a => valor ? MoMi(IA(a).VA, a) : IA(a).A).Select(v => v is > 0 ? v : null).ToArray(), AxCores.Azul),
                new AxSerie(T("Unclassified", "Não classificadas"), AnosP.Select(a => valor ? MoMi(IA(a).VNC, a) : IA(a).NC).Select(v => v is > 0 ? v : null).ToArray(), AxCores.Neutro),
            },
            Descricao = valor ? T("Value by status", "Valor por status") : T("Proposals by status", "Propostas por status"),
        };
        long w = (XR - XL - 182880) / 2;
        Grafico(s, XL, Y0, w, YB - Y0, T("Number of proposals by status", "Número de propostas por status"), Pilha(false));
        Grafico(s, XL + w + 182880, Y0, w, YB - Y0, T($"Value by status ({Sim} millions)", $"Valor por status ({Sim} milhões)"), Pilha(true));
        var ip = IP(R);
        var abC = Ind(C, 1, 12); var abR = Ind(R, 1, Mr);
        var aberto = (Mo(abC.VA, C) ?? 0) + (Mo(abR.VA, R) ?? 0);
        var antigos = _m.Mes.Where(kv => new DateTime(kv.Key.Ano, kv.Key.Mes, 1).AddMonths(1) <= Corte.AddDays(1).AddMonths(-6)).ToList();
        var velhoN = antigos.Sum(kv => kv.Value.A); var velhoV = antigos.Sum(kv => Mo(kv.Value.VA, kv.Key.Ano) ?? 0);
        var txt = T($"{Din(aberto)} open in {N(abC.A + abR.A)} proposals sent in {C}–{R}; {Din(velhoV)} ({N(velhoN)} proposals) were sent more than 6 months before the cut-off.",
                    $"{Din(aberto)} em aberto em {N(abC.A + abR.A)} propostas enviadas em {C}–{R}; {Din(velhoV)} ({N(velhoN)} propostas) foram enviadas há mais de 6 meses do corte.");
        Barra(s, T("OPEN PIPELINE", "PIPELINE ABERTO"), txt);
        s.Notas = Notas(T("Show where the quoted value stands today.", "Mostrar onde está hoje o valor ofertado."), txt,
            T("Stacked bars by send year; status as of extraction.", "Barras empilhadas por ano de envio; status na extração."),
            T("Old open proposals are less likely to convert and inflate the pipeline.", "Abertas antigas têm menor chance de conversão e inflam o pipeline."),
            T("Review open proposals older than 6 months: confirm, update status or close.", "Revisar abertas com mais de 6 meses: confirmar, atualizar o status ou encerrar."),
            T("Unclassified = status not mapped to won/lost/open in the source.", "Não classificadas = status sem correspondência com ganha/perdida/aberta na origem."));
        return s;
    }

    private AxSlide? Perdas()
    {
        var ip = IP(R);
        if (AnosP.All(a => IA(a).P == 0)) { Omitir("perdas", T("Lost Business", "Perdas"), Indisp); return null; }
        var s = Novo("perdas", T("Lost Business | Where are we losing value?", "Perdas | Onde estamos perdendo valor?"),
            T($"Lost proposals by send year · concentration in {PerR} · loss reasons are not recorded in the data", $"Propostas perdidas por ano de envio · concentração em {PerR} · os motivos de perda não estão na base"));
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = AnosP.Select(RotAno).ToArray(), Formato = FmtMi,
            Series =
            {
                new AxSerie(T("Lost value", "Valor perdido"), AnosP.Select(a => MoMi(IA(a).VP, a)).ToArray(), AxCores.Vermelho),
                new AxSerie(T("Lost proposals", "Propostas perdidas"), AnosP.Select(a => IA(a).P > 0 ? (double?)IA(a).P : null).ToArray(), AxCores.Escuro) { Linha = true, Formato = FmtQtd },
            },
            Descricao = T("Lost value and proposals by year", "Valor e propostas perdidas por ano"),
        };
        long wg = 5029200;
        Grafico(s, XL, Y0, wg, YB - Y0, T($"Lost value ({Sim} millions) and lost proposals", $"Valor perdido ({Sim} milhões) e propostas perdidas"), ch,
            T("Lost includes cancelled (CRM status 'Cancelled').", "Perdidas incluem as canceladas (status 'Cancelled' no CRM)."));
        var dim = _o.Escopo == "bu" ? "segmento" : "bu";
        var nomeDim = dim == "bu" ? "BU" : T("Segment", "Segmento");
        var lst = _m.Dim[dim].Chaves.Select(k => (k, i: DimP(dim, k, R))).Where(x => x.i.P > 0).OrderByDescending(x => x.i.VP).ToList();
        var totVp = lst.Sum(x => x.i.VP);
        var linhas = new List<AxCelula[]> { new[] { Cab(nomeDim, "l"), Cab(T("Lost", "Perdidas"), "r"), Cab(T("Lost value", "Valor perdido"), "r"), Cab(T("Share", "Part."), "r"), Cab(T("Loss rate", "Taxa perda"), "r") } };
        foreach (var (k, i) in lst.Take(7))
            linhas.Add(new[] { Rot(Curto(k, 26)), Num(N(i.P)), Num(Din(Mo(i.VP, R))), Num(Pct(totVp > 0 ? i.VP / totVp * 100 : null)), Num(Pct(i.Decididas > 0 ? i.P * 100.0 / i.Decididas : null)) });
        var xt = XL + wg + 182880;
        s.Elems.Add(Txt(xt, Y0, XR - xt, 228600, new AxRun(T($"Where losses concentrate · {PerR} (by lost value)", $"Onde as perdas se concentram · {PerR} (por valor perdido)"), 1100, true, AxCores.Escuro)));
        s.Elems.Add(new AxTabela(xt, Y0 + 320040, XR - xt, 338328L * linhas.Count) { Pesos = new[] { 2.6, 1, 1.5, 1, 1.1 }, Linhas = linhas, Tam = 900, Nome = "Loss concentration table" });
        var topo = lst.FirstOrDefault();
        var txt = topo.k is null ? T($"No lost proposals in {PerR}.", $"Sem propostas perdidas em {PerR}.")
            : T($"{PerR}: {Din(Mo(ip.VP, R))} lost in {N(ip.P)} proposals ({N(ip.Canceladas)} cancelled); {topo.k} concentrates {Pct(totVp > 0 ? topo.i.VP / totVp * 100 : null)} of the lost value.",
                $"{PerR}: {Din(Mo(ip.VP, R))} perdidos em {N(ip.P)} propostas ({N(ip.Canceladas)} canceladas); {topo.k} concentra {Pct(totVp > 0 ? topo.i.VP / totVp * 100 : null)} do valor perdido.");
        Barra(s, T("LOSS CONCENTRATION", "CONCENTRAÇÃO"), txt);
        s.Notas = Notas(T("Size and locate lost value without inventing reasons.", "Dimensionar e localizar o valor perdido sem inventar motivos."), txt,
            T("Table ranks by lost value; loss rate = lost ÷ decided.", "Tabela ordenada por valor perdido; taxa de perda = perdidas ÷ decididas."),
            T("Concentrated losses point to where a win/loss review pays off most.", "Perdas concentradas indicam onde uma análise de ganhos/perdas rende mais."),
            T("Run a structured loss review where the value is concentrated; record loss reasons going forward.", "Fazer uma revisão estruturada de perdas onde o valor se concentra; registrar os motivos de perda daqui em diante."),
            T("Loss reasons are NOT available in the source data; none are inferred here.", "Os motivos de perda NÃO estão na base; nenhum é presumido aqui."));
        return s;
    }

    private AxSlide? Metas()
    {
        if (Escopado) { Omitir("metas", T("Target Achievement", "Atingimento de metas"), T("targets exist only as annual company-wide budgets (HSA/Group)", "as metas são só budgets anuais da empresa toda (HSA/Grupo)")); return null; }
        var anos = Anos.Where(a => _m.Metas.TryGetValue(a, out var x) && (x.Hsa is not null || x.Grupo is not null)).ToList();
        if (anos.Count == 0) { Omitir("metas", T("Target Achievement", "Atingimento de metas"), T("no targets registered", "nenhuma meta cadastrada")); return null; }
        var s = Novo("metas", T("Target Achievement | How far are we from the target?", "Atingimento de metas | Qual a distância da meta?"),
            T($"Annual targets (HSA and Group) vs sales · {R} up to {(UltV > 0 ? Mn(UltV) : "—")} against the full-year target", $"Metas anuais (HSA e Grupo) × vendas · {R} até {(UltV > 0 ? Mn(UltV) : "—")} contra a meta do ano inteiro"));
        decimal? Vano(int a) => Ven(a, 1, a == R ? UltV : 12);
        var temGrupo = anos.Any(a => _m.Metas[a].Grupo is not null);
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = anos.Select(a => a == R && UltV < 12 ? T($"{a} YTD", $"{a} acum.") : a.ToString()).ToArray(), Formato = FmtMi,
            // vendas por último: os rótulos delas ficam por cima das colunas de meta
            Series = new List<AxSerie> { new(T("HSA target", "Meta HSA"), anos.Select(a => MoMi(_m.Metas[a].Hsa, a)).ToArray(), AxCores.Escuro) { Rotulos = false } }
                .Concat(temGrupo ? new[] { new AxSerie(T("Group target", "Meta Grupo"), anos.Select(a => MoMi(_m.Metas[a].Grupo, a)).ToArray(), AxCores.Neutro) { Rotulos = false } } : Array.Empty<AxSerie>())
                .Append(new AxSerie(T("Sales", "Vendas"), anos.Select(a => MoMi(Vano(a), a)).ToArray(), AxCores.Verde)).ToList(),
            Descricao = T("Sales vs targets by year", "Vendas × metas por ano"),
        };
        long wg = 6217920;
        Grafico(s, XL, Y0, wg, YB - Y0, T($"Sales vs annual targets ({Sim} millions)", $"Vendas × metas anuais ({Sim} milhões)"), ch,
            T("Labels on sales; target values and attainment in the table.", "Rótulos nas vendas; metas e atingimento na tabela."));
        var linhas = new List<AxCelula[]> { new[] { Cab(T("Year", "Ano"), "l"), Cab(T("HSA target", "Meta HSA"), "r"), Cab(T("Sales", "Vendas"), "r"), Cab(T("Att. HSA", "Ating. HSA"), "r"), Cab(T("Gap HSA", "Gap HSA"), "r") }
            .Concat(temGrupo ? new[] { Cab(T("Att. Group", "Ating. Grupo"), "r") } : Array.Empty<AxCelula>()).ToArray() };
        foreach (var a in anos)
        {
            var mt = Mo(_m.Metas[a].Hsa, a); var v = Mo(Vano(a), a);
            var at = mt is > 0 && v is decimal vv ? (double?)(double)(vv / mt.Value * 100) : null;
            var mg = Mo(_m.Metas[a].Grupo, a);
            var ag = mg is > 0 && v is decimal vg ? (double?)(double)(vg / mg.Value * 100) : null;
            linhas.Add(new[] { Rot(a == R && UltV < 12 ? T($"{a} YTD", $"{a} acum.") : a.ToString(), a == R), Num(Din(mt)), Num(Din(v)), Num(Pct(at), at is >= 100 ? AxCores.VerdeMedio : at is not null ? AxCores.Vermelho : null, true), Num(mt is decimal m1 && v is decimal v1 ? Din(v1 - m1) : NA) }
                .Concat(temGrupo ? new[] { Num(Pct(ag), ag is >= 100 ? AxCores.VerdeMedio : ag is not null ? AxCores.Vermelho : null, true) } : Array.Empty<AxCelula>()).ToArray());
        }
        var xt = XL + wg + 182880;
        s.Elems.Add(new AxTabela(xt, Y0, XR - xt, 320040L * linhas.Count) { Pesos = temGrupo ? new[] { 1.2, 1.4, 1.4, 1, 1.4, 1.1 } : new[] { 1.2, 1.4, 1.4, 1, 1.4 }, Linhas = linhas, Tam = temGrupo ? 850 : 900, Nome = "Targets table" });
        string txt;
        var metaR = _m.Metas.TryGetValue(R, out var mr) ? Mo(mr.Hsa, R) : null; var vR = Mo(Vano(R), R);
        if (metaR is decimal m2 && m2 > 0 && vR is decimal v2 && UltV > 0)
        {
            var falta = m2 - v2; var restantes = 12 - UltV;
            txt = falta <= 0 ? T($"{R}: HSA target reached ({Pct((double)(v2 / m2 * 100))}).", $"{R}: meta HSA atingida ({Pct((double)(v2 / m2 * 100))}).")
                : restantes > 0
                    ? T($"{R}: {Pct((double)(v2 / m2 * 100))} of the HSA target; {Din(falta)} to go = {Din(falta / restantes)}/month in the remaining {restantes} months vs {Din(v2 / UltV)}/month so far.",
                        $"{R}: {Pct((double)(v2 / m2 * 100))} da meta HSA; faltam {Din(falta)} = {Din(falta / restantes)}/mês nos {restantes} meses restantes, contra {Din(v2 / UltV)}/mês até agora.")
                    : T($"{R}: {Pct((double)(v2 / m2 * 100))} of the HSA target; gap {Din(-falta)}.", $"{R}: {Pct((double)(v2 / m2 * 100))} da meta HSA; gap de {Din(-falta)}.");
        }
        else txt = T($"No HSA target registered for {R}.", $"Sem meta HSA cadastrada para {R}.");
        Barra(s, T("RUN-RATE", "RITMO"), txt);
        s.Notas = Notas(T("Compare sales with the annual targets.", "Comparar vendas com as metas anuais."), txt,
            T("Attainment = sales ÷ annual target; the current year is partial (year to date vs full-year target).", "Atingimento = vendas ÷ meta anual; o ano atual é parcial (acumulado contra a meta do ano inteiro)."),
            T("The required monthly run-rate is simple arithmetic (remaining gap ÷ remaining months), not a forecast.", "O ritmo mensal necessário é aritmética simples (gap restante ÷ meses restantes), não uma previsão."),
            T("Agree on actions to close the gap where the pipeline allows it.", "Definir ações para fechar o gap onde o pipeline permitir."),
            T("There are no monthly, BU or sector targets in the source.", "Não há metas mensais, por BU ou por setor na base."));
        return s;
    }

    private AxSlide? AfmNb()
    {
        var a = DimP("setor", ConversaoService.Afm, R); var n = DimP("setor", ConversaoService.Nb, R);
        if (a.Vazio && n.Vazio) { Omitir("afmnb", T("AFM vs NB", "AFM × NB"), Indisp); return null; }
        var ac = DimP("setor", ConversaoService.Afm, C); var nc = DimP("setor", ConversaoService.Nb, C);
        var s = Novo("afmnb", T("Aftermarket vs New Business | Who delivers the result?", "Aftermarket × New Business | Quem entrega o resultado?"),
            T($"{PerR}{(Cmp ? $" vs {PerC}" : "")} · proposals by send date · won value = proposals won (not billed sales)", $"{PerR}{(Cmp ? $" × {PerC}" : "")} · propostas pela data de envio · valor ganho = propostas ganhas (não é venda faturada)"));
        var linhas = new List<AxCelula[]> { new[] { Cab(T("Indicator", "Indicador"), "l"), Cab("AFM", "r"), Cab("NB", "r") }.Concat(Cmp ? new[] { Cab(T("AFM vs PY", "AFM vs AA"), "r"), Cab(T("NB vs PY", "NB vs AA"), "r") } : Array.Empty<AxCelula>()).ToArray() };
        void L(string nome, Func<Indicadores, int, string> f, Func<Indicadores, Indicadores, int, int, double?>? d = null, bool pp = false)
        {
            var c = new List<AxCelula> { Rot(nome) , Num(f(a, R), null, true), Num(f(n, R), null, true) };
            if (Cmp)
            {
                var da = d?.Invoke(a, ac, R, C); var dn = d?.Invoke(n, nc, R, C);
                c.Add(Num(d is null ? "" : pp ? Pp(da) : Var(da), CorVar(da))); c.Add(Num(d is null ? "" : pp ? Pp(dn) : Var(dn), CorVar(dn)));
            }
            linhas.Add(c.ToArray());
        }
        L(T("Proposals sent", "Propostas enviadas"), (i, _) => N(i.Enviadas), (x, y, _, _) => VarPct(x.Enviadas, (double)y.Enviadas));
        L(T("Quoted value", "Valor ofertado"), (i, ano) => Din(Mo(i.ValorEnviado, ano)), (x, y, r, c) => VarPct(Mo(x.ValorEnviado, r), Mo(y.ValorEnviado, c)));
        L(T("Won proposals", "Propostas ganhas"), (i, _) => N(i.G), (x, y, _, _) => VarPct(x.G, (double)y.G));
        L(T("Won value", "Valor ganho"), (i, ano) => Din(Mo(i.VG, ano)), (x, y, r, c) => VarPct(Mo(x.VG, r), Mo(y.VG, c)));
        L(T("Conversion (qty)", "Conversão (qtd)"), (i, _) => Pct(i.TaxaQtd), (x, y, _, _) => Dif(x.TaxaQtd, y.TaxaQtd), true);
        L(T("Conversion (value)", "Conversão (valor)"), (i, _) => Pct(i.TaxaValor), (x, y, _, _) => Dif(x.TaxaValor, y.TaxaValor), true);
        L(T("Avg. quoted value", "Valor médio ofertado"), (i, ano) => Din(Mo(i.ValorMedio, ano)), (x, y, r, c) => VarPct(Mo(x.ValorMedio, r), Mo(y.ValorMedio, c)));
        L(T("Open value", "Valor em aberto"), (i, ano) => Din(Mo(i.VA, ano)));
        long wt = 6309360;
        s.Elems.Add(new AxTabela(XL, Y0, wt, 393192L * linhas.Count) { Pesos = Cmp ? new[] { 2.4, 1.3, 1.3, 1.1, 1.1 } : new[] { 2.4, 1.3, 1.3 }, Linhas = linhas, Tam = 950, Nome = "AFM vs NB table" });
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = AnosP.Select(RotAno).ToArray(), Formato = FmtMi,
            Series =
            {
                new AxSerie("AFM", AnosP.Select(y => MoMi(_m.Soma(_m.Dim["setor"].Celulas.Where(kv => kv.Key.Chave == ConversaoService.Afm && kv.Key.Ano == y && kv.Key.Mes <= AteA).Select(kv => kv.Value)).VG, y)).ToArray(), AxCores.VerdeMedio),
                new AxSerie("NB", AnosP.Select(y => MoMi(_m.Soma(_m.Dim["setor"].Celulas.Where(kv => kv.Key.Chave == ConversaoService.Nb && kv.Key.Ano == y && kv.Key.Mes <= AteA).Select(kv => kv.Value)).VG, y)).ToArray(), AxCores.Azul),
            },
            Descricao = T("Won value by sector and year", "Valor ganho por setor e ano"),
        };
        var xg = XL + wt + 182880;
        Grafico(s, xg, Y0, XR - xg, YB - Y0, T($"Won value by year ({Sim} millions)", $"Valor ganho por ano ({Sim} milhões)"), ch, T($"Basis: {BaseAnualTxt}.", $"Base: {BaseAnualTxt}."));
        var vol = a.VG >= n.VG ? "AFM" : "NB"; var efi = (a.TaxaQtd ?? 0) >= (n.TaxaQtd ?? 0) ? "AFM" : "NB";
        var txt = vol == efi
            ? T($"{vol} leads in both won value ({Din(Mo(vol == "AFM" ? a.VG : n.VG, R))}) and conversion ({Pct(vol == "AFM" ? a.TaxaQtd : n.TaxaQtd)}) in {PerR}.", $"{vol} lidera em valor ganho ({Din(Mo(vol == "AFM" ? a.VG : n.VG, R))}) e em conversão ({Pct(vol == "AFM" ? a.TaxaQtd : n.TaxaQtd)}) em {PerR}.")
            : T($"{vol} leads in won value, {efi} converts more ({Pct(a.TaxaQtd)} AFM vs {Pct(n.TaxaQtd)} NB) in {PerR}: volume and efficiency are different measures.", $"{vol} lidera em valor ganho, {efi} converte mais ({Pct(a.TaxaQtd)} AFM × {Pct(n.TaxaQtd)} NB) em {PerR}: volume e eficiência são medidas diferentes.");
        Barra(s, T("READING", "LEITURA"), txt);
        s.Notas = Notas(T("Compare the two commercial engines.", "Comparar os dois motores comerciais."), txt,
            T("Table: period indicators; chart: won value history.", "Tabela: indicadores do período; gráfico: histórico do valor ganho."),
            T("Each sector has its own ticket size and sales cycle; compare rates, not only totals.", "Cada setor tem ticket e ciclo próprios; comparar taxas, não só totais."),
            T("Set sector-specific conversion goals and follow-up cadence.", "Definir metas de conversão e cadência de follow-up por setor."),
            T("AFM values in USD are converted to BRL at the annual rate.", "Valores AFM em US$ convertidos para R$ pela taxa anual."));
        return s;
    }

    private AxSlide? Dimensao(string dim, string nome, string pergunta)
    {
        if (_o.Escopo == dim) { Omitir(dim, nome, T("the deck is already filtered by this dimension", "a apresentação já é desse recorte")); return null; }
        var lst = _m.Dim[dim].Chaves.Select(k => (k, i: DimP(dim, k, R), c: DimP(dim, k, C))).Where(x => !x.i.Vazio).ToList();
        if (lst.Count < 2 || (lst.Sum(x => x.i.VG) <= 0 && lst.Sum(x => (double)x.i.ValorEnviado) <= 0))
        { Omitir(dim, nome, T("not enough data in the period", "dados insuficientes no período")); return null; }
        var ord = lst.OrderByDescending(x => x.i.VG).ThenByDescending(x => x.i.ValorEnviado).ToList();
        var top = ord.Take(8).ToList();
        var outros = ord.Skip(8).ToList();
        var totVg = ord.Sum(x => x.i.VG);
        var s = Novo(dim, $"{nome} | {pergunta}", T($"{PerR}{(Cmp ? $" vs {PerC}" : "")} · ranked by won value (proposals won, not billed sales) · top 8", $"{PerR}{(Cmp ? $" × {PerC}" : "")} · ordenado por valor ganho (propostas ganhas, não venda faturada) · top 8"));
        var cats = top.Select(x => Curto(x.k, 28)).ToList(); var vals = top.Select(x => MoMi(x.i.VG, R)).ToList();
        if (outros.Count > 0) { cats.Add(T($"Others ({outros.Count})", $"Outros ({outros.Count})")); vals.Add(MoMi(outros.Sum(x => x.i.VG), R)); }
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Barras, Categorias = cats.ToArray(), Formato = FmtMi, Legenda = false, Espaco = 45,
            Series = { new AxSerie(T("Won value", "Valor ganho"), vals.ToArray(), AxCores.VerdeMedio) { CoresPontos = cats.Select((_, i) => i == cats.Count - 1 && outros.Count > 0 ? AxCores.Neutro : null).ToArray() } },
            Descricao = T($"Won value by {nome}", $"Valor ganho por {nome}"),
        };
        long wg = 4389120;
        Grafico(s, XL, Y0, wg, YB - Y0, T($"Won value · {PerR} ({Sim} millions)", $"Valor ganho · {PerR} ({Sim} milhões)"), ch);
        var linhas = new List<AxCelula[]> { new[] { Cab(nome, "l"), Cab(T("Sent", "Enviadas"), "r"), Cab(T("Quoted", "Ofertado"), "r"), Cab(T("Won value", "Valor ganho"), "r"), Cab(T("Conv. qty", "Conv. qtd"), "r") }
            .Concat(Cmp ? new[] { Cab(T("Δ p.p.", "Δ p.p."), "r") } : Array.Empty<AxCelula>()).Append(Cab(T("Share", "Part."), "r")).ToArray() };
        foreach (var (k, i, c) in top)
        {
            var d = Dif(i.TaxaQtd, c.TaxaQtd);
            var cel = new List<AxCelula> { Rot(Curto(k, 24)), Num(N(i.Enviadas)), Num(Din(Mo(i.ValorEnviado, R))), Num(Din(Mo(i.VG, R)), null, true), Num(Pct(i.TaxaQtd)) };
            if (Cmp) cel.Add(Num(Pp(d), CorVar(d)));
            cel.Add(Num(Pct(totVg > 0 ? i.VG / totVg * 100 : null)));
            linhas.Add(cel.ToArray());
        }
        var xt = XL + wg + 182880;
        s.Elems.Add(new AxTabela(xt, Y0, XR - xt, Math.Min(YB - Y0, 365760L * linhas.Count)) { Pesos = Cmp ? new[] { 2.4, 0.9, 1.2, 1.3, 1, 0.9, 0.9 } : new[] { 2.4, 0.9, 1.2, 1.3, 1, 0.9 }, Linhas = linhas, Tam = 850, Nome = nome + " table" });
        var t1 = ord.First();
        var mud = Cmp ? top.Where(x => Dif(x.i.TaxaQtd, x.c.TaxaQtd) is not null && x.i.Decididas >= 5).OrderBy(x => Dif(x.i.TaxaQtd, x.c.TaxaQtd)).FirstOrDefault() : default;
        var txt = T($"{t1.k} contributes {Pct(totVg > 0 ? t1.i.VG / totVg * 100 : null)} of the won value ({Din(Mo(t1.i.VG, R))}); top 3 = {Pct(totVg > 0 ? ord.Take(3).Sum(x => x.i.VG) / totVg * 100 : null)}.",
                    $"{t1.k} responde por {Pct(totVg > 0 ? t1.i.VG / totVg * 100 : null)} do valor ganho ({Din(Mo(t1.i.VG, R))}); top 3 = {Pct(totVg > 0 ? ord.Take(3).Sum(x => x.i.VG) / totVg * 100 : null)}.")
                  + (mud.k is not null && Dif(mud.i.TaxaQtd, mud.c.TaxaQtd) is < -0.05 ? T($" Largest conversion drop: {mud.k} ({Pp(Dif(mud.i.TaxaQtd, mud.c.TaxaQtd))}).", $" Maior queda de conversão: {mud.k} ({Pp(Dif(mud.i.TaxaQtd, mud.c.TaxaQtd))}).") : "");
        Barra(s, T("CONCENTRATION", "CONCENTRAÇÃO"), txt);
        s.Notas = Notas(T($"Show where value is won by {nome}.", $"Mostrar onde o valor é ganho por {nome}."), txt,
            T("Won value = proposals won (there are no billed sales by this dimension).", "Valor ganho = propostas ganhas (não há venda faturada por esta dimensão)."),
            T("High concentration increases dependency on few units; low conversion with high quoted value signals qualification issues.", "Alta concentração aumenta a dependência de poucas unidades; conversão baixa com muito valor ofertado sinaliza problema de qualificação."),
            T("Focus follow-up on large units with falling conversion.", "Concentrar o follow-up nas unidades grandes com conversão em queda."),
            T("'Não informado' = field empty in the source spreadsheets.", "'Não informado' = campo vazio nas planilhas de origem."));
        return s;
    }

    private AxSlide? Capacidade()
    {
        var ip = IP(R); var ic = IP(C);
        int Ativos(int ano) => _m.Dim["vendedor"].Celulas.Where(kv => kv.Key.Ano == ano && kv.Key.Mes >= De && kv.Key.Mes <= Ate && kv.Value.Enviadas > 0 && kv.Key.Chave != ConversaoService.NaoInformado).Select(kv => kv.Key.Chave).Distinct().Count();
        var ar = Ativos(R); var acn = Ativos(C);
        if (ar == 0 || ip.Enviadas == 0) { Omitir("capacidade", T("Capacity & Productivity", "Capacidade e produtividade"), Indisp); return null; }
        var s = Novo("capacidade", T("Capacity & Productivity | Is the team's effort turning into results?", "Capacidade e produtividade | O esforço da equipe vira resultado?"),
            T($"{PerR}{(Cmp ? $" vs {PerC}" : "")} · aggregated team indicators (no individual performance shown)", $"{PerR}{(Cmp ? $" × {PerC}" : "")} · indicadores agregados da equipe (sem desempenho individual)"));
        long w = 2624328, g = 155448, y = Y0, h = 1005840;
        string Dv(double? v) => Cmp ? T($"vs {PerC}: {Var(v)}", $"vs {PerC}: {Var(v)}") : "";
        Kpi(s, XL, y, w, h, T("ACTIVE QUOTERS", "EMISSORES ATIVOS"), N(ar), Cmp ? T($"{N(acn)} in {PerC}", $"{N(acn)} em {PerC}") : T("with ≥1 proposal sent", "com ≥1 proposta enviada"), AxCores.Azul);
        Kpi(s, XL + (w + g), y, w, h, T("PROPOSALS PER QUOTER", "PROPOSTAS POR EMISSOR"), N1(ip.Enviadas / (double)ar), Dv(acn > 0 ? VarPct(ip.Enviadas / (double)ar, ic.Enviadas / (double)acn) : null), AxCores.VerdeMedio);
        Kpi(s, XL + 2 * (w + g), y, w, h, T("QUOTED VALUE PER QUOTER", "OFERTADO POR EMISSOR"), Din(Mo(ip.ValorEnviado / ar, R)), Dv(acn > 0 ? VarPct(Mo(ip.ValorEnviado / ar, R), Mo(ic.ValorEnviado / acn, C)) : null), AxCores.Azul);
        Kpi(s, XL + 3 * (w + g), y, w, h, T("WON VALUE PER QUOTER", "GANHO POR EMISSOR"), Din(Mo((decimal)ip.VG / ar, R)), Dv(acn > 0 ? VarPct(Mo((decimal)ip.VG / ar, R), Mo((decimal)ic.VG / acn, C)) : null), AxCores.VerdeMedio);
        var meses = Enumerable.Range(1, 12).ToArray();
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = meses.Select(Mn).ToArray(), Formato = FmtQtd, Espaco = 40,
            Series =
            {
                new AxSerie(C.ToString(), meses.Select(mm => _m.Mes.TryGetValue((C, mm), out var i) && i.Enviadas > 0 ? (double?)i.Enviadas : null).ToArray(), AxCores.Neutro),
                new AxSerie(R.ToString(), meses.Select(mm => mm <= Mr && _m.Mes.TryGetValue((R, mm), out var i) && i.Enviadas > 0 ? (double?)i.Enviadas : null).ToArray(), AxCores.Verde),
            },
            Descricao = T("Proposals sent per month", "Propostas enviadas por mês"),
        };
        Grafico(s, XL, y + h + 137160, XR - XL, YB - (y + h + 137160), T("Proposals sent per month (workload)", "Propostas enviadas por mês (carga de trabalho)"), ch);
        var txt = T($"{N(ar)} active quoters sent {N(ip.Enviadas)} proposals in {PerR} ({N1(ip.Enviadas / (double)ar)} each, {N1(ip.Enviadas / (double)Math.Max(1, Ate - De + 1))} per month in total).",
                    $"{N(ar)} emissores ativos enviaram {N(ip.Enviadas)} propostas em {PerR} ({N1(ip.Enviadas / (double)ar)} cada, {N1(ip.Enviadas / (double)Math.Max(1, Ate - De + 1))} por mês no total).");
        Barra(s, T("CAPACITY", "CAPACIDADE"), txt);
        s.Notas = Notas(T("Show team workload and productivity in aggregate.", "Mostrar carga e produtividade da equipe de forma agregada."), txt,
            T("Active quoter = person with at least one proposal sent in the period (count only; names are not shown).", "Emissor ativo = pessoa com ao menos uma proposta enviada no período (só a contagem; nomes não aparecem)."),
            T("More proposals per person without more won value suggests effort is going to low-probability opportunities.", "Mais propostas por pessoa sem mais valor ganho sugere esforço em oportunidades de baixa probabilidade."),
            T("Balance workload and qualification before adding volume.", "Equilibrar carga e qualificação antes de aumentar volume."));
        return s;
    }

    // ---------- oportunidades, cenários, plano e conclusões ----------

    private sealed record Oport(string Titulo, string Evidencia, string Acao, string Impacto, string Indicador);

    private List<Oport>? _ops;
    private List<Oport> Oportunidades_()
    {
        if (_ops is not null) return _ops;
        var l = new List<Oport>();
        var ip = IP(R);
        // 1. pipeline aberto (enviadas no ano anterior e no atual)
        var abC = Ind(C, 1, 12); var abR = Ind(R, 1, Mr);
        var aberto = (Mo(abC.VA, C) ?? 0) + (Mo(abR.VA, R) ?? 0);
        var taxaHist = abC.TaxaValorDecididas;
        if (aberto > 0)
            l.Add(new(T("Convert the open pipeline", "Converter o pipeline aberto"),
                T($"{Din(aberto)} open in {N(abC.A + abR.A)} proposals sent in {C}–{R}.", $"{Din(aberto)} em aberto em {N(abC.A + abR.A)} propostas enviadas em {C}–{R}."),
                T("Review open proposals by value and age; define next step and follow-up date for each.", "Revisar as abertas por valor e idade; definir próximo passo e data de follow-up para cada uma."),
                taxaHist is double th ? T($"At the {C} value conversion ({Pct(th)}): ≈ {Din(aberto * (decimal)th / 100)} (simulation).", $"Na conversão por valor de {C} ({Pct(th)}): ≈ {Din(aberto * (decimal)th / 100)} (simulação).") : Indisp,
                T("Open value and conversion of open proposals", "Valor em aberto e conversão das abertas")));
        // 2. perdas concentradas
        var dim = _o.Escopo == "bu" ? "segmento" : "bu";
        var perdas = _m.Dim[dim].Chaves.Select(k => (k, i: DimP(dim, k, R))).Where(x => x.i.VP > 0).OrderByDescending(x => x.i.VP).ToList();
        var totVp = perdas.Sum(x => x.i.VP);
        if (perdas.Count > 0 && totVp > 0)
        {
            var t = perdas[0];
            l.Add(new(T($"Recover value lost in {Curto(t.k, 22)}", $"Recuperar valor perdido em {Curto(t.k, 22)}"),
                T($"{t.k} concentrates {Pct(t.i.VP / totVp * 100)} of the value lost in {PerR} ({Din(Mo(t.i.VP, R))}).", $"{t.k} concentra {Pct(t.i.VP / totVp * 100)} do valor perdido em {PerR} ({Din(Mo(t.i.VP, R))})."),
                T("Run a win/loss review with the team; start recording loss reasons (not available today).", "Fazer revisão de ganhos/perdas com a equipe; passar a registrar os motivos de perda (hoje não existem na base)."),
                T($"Recovering 10% of that value ≈ {Din(Mo(t.i.VP * 0.10, R))} (simulation).", $"Recuperar 10% desse valor ≈ {Din(Mo(t.i.VP * 0.10, R))} (simulação)."),
                T("Lost value and loss rate in the unit", "Valor perdido e taxa de perda na unidade")));
        }
        // 3. segmento com muito valor decidido e conversão abaixo da média
        if (_o.Escopo != "segmento" && ip.TaxaValorDecididas is double media)
        {
            var dec = ip.VG + ip.VP;
            var alvo = _m.Dim["segmento"].Chaves.Select(k => (k, i: DimP("segmento", k, R)))
                .Where(x => dec > 0 && (x.i.VG + x.i.VP) / dec >= 0.08 && x.i.TaxaValorDecididas is double tx && tx < media - 1)
                .OrderByDescending(x => (media - x.i.TaxaValorDecididas!.Value) * (x.i.VG + x.i.VP)).FirstOrDefault();
            if (alvo.k is not null)
            {
                var ganho = (media - alvo.i.TaxaValorDecididas!.Value) / 100 * (alvo.i.VG + alvo.i.VP);
                l.Add(new(T($"Lift conversion in {Curto(alvo.k, 22)}", $"Elevar a conversão em {Curto(alvo.k, 22)}"),
                    T($"{alvo.k}: {Pct((alvo.i.VG + alvo.i.VP) / dec * 100)} of the decided value, value conversion {Pct(alvo.i.TaxaValorDecididas)} vs {Pct(media)} overall.", $"{alvo.k}: {Pct((alvo.i.VG + alvo.i.VP) / dec * 100)} do valor decidido, conversão por valor {Pct(alvo.i.TaxaValorDecididas)} × {Pct(media)} no total."),
                    T("Review qualification, pricing and technical fit before quoting in this segment.", "Revisar qualificação, preço e aderência técnica antes de ofertar neste segmento."),
                    T($"Reaching the overall rate on the same base ≈ {Din(Mo(ganho, R))} more won value (simulation).", $"Chegar à taxa geral na mesma base ≈ {Din(Mo(ganho, R))} a mais em valor ganho (simulação)."),
                    T("Value conversion of the segment", "Conversão por valor do segmento")));
            }
        }
        // 4. qualidade dos dados
        var semBu = DimP("bu", ConversaoService.NaoInformado, R);
        if (ip.NC > 0 || (ip.ValorEnviado > 0 && semBu.ValorEnviado / ip.ValorEnviado >= 0.05m))
            l.Add(new(T("Improve CRM data quality", "Melhorar a qualidade dos dados do CRM"),
                T($"{N(ip.NC)} proposals in {PerR} without a status mapped to won/lost/open; {Pct(ip.ValorEnviado > 0 ? (double)(semBu.ValorEnviado / ip.ValorEnviado * 100) : null)} of the quoted value without BU.",
                  $"{N(ip.NC)} propostas de {PerR} sem status de ganha/perdida/aberta; {Pct(ip.ValorEnviado > 0 ? (double)(semBu.ValorEnviado / ip.ValorEnviado * 100) : null)} do valor ofertado sem BU."),
                T("Make status, BU, segment and product mandatory; review monthly.", "Tornar status, BU, segmento e produto obrigatórios; revisar mensalmente."),
                T("More reliable conversion and pipeline readings (not a financial impact).", "Leituras de conversão e pipeline mais confiáveis (sem impacto financeiro direto)."),
                T("% of proposals with complete fields", "% de propostas com campos completos")));
        return _ops = l;
    }

    private AxSlide? Oportunidades()
    {
        var ops = Oportunidades_();
        if (ops.Count == 0) { Omitir("oportunidades", T("Revenue Growth Opportunities", "Oportunidades de crescimento"), Indisp); return null; }
        var s = Novo("oportunidades", T("Revenue Growth Opportunities | Where can we sell more?", "Oportunidades de crescimento | Onde vender mais?"),
            T("Derived only from the data in this deck · potential impacts are simulations, not forecasts", "Derivadas só dos dados desta apresentação · impactos potenciais são simulações, não previsões"));
        var linhas = new List<AxCelula[]> { new[] { Cab(T("Opportunity", "Oportunidade"), "l"), Cab(T("Evidence", "Evidência"), "l"), Cab(T("Action", "Ação"), "l"), Cab(T("Potential impact", "Impacto potencial"), "l") } };
        foreach (var o in ops) linhas.Add(new[] { new AxCelula(o.Titulo, true, AxCores.Escuro, AxCores.CardLinha), Rot(o.Evidencia), Rot(o.Acao), new AxCelula(o.Impacto, false, AxCores.VerdeMedio) });
        var hl = Math.Min(960120L, (YB - Y0 - 320040) / Math.Max(1, ops.Count));
        s.Elems.Add(new AxTabela(XL, Y0, XR - XL, 320040 + hl * ops.Count) { Pesos = new[] { 1.5, 2.6, 2.6, 2.0 }, Linhas = linhas, Tam = ops.Count > 3 ? 900 : 1000, Nome = "Opportunities table" });
        var txt = T("Prioritise by potential impact and ease of execution; validate every simulated figure with the business before committing.", "Priorizar por impacto potencial e facilidade de execução; validar cada valor simulado com o negócio antes de assumir compromissos.");
        Barra(s, T("HOW TO READ", "COMO LER"), txt);
        s.Notas = Notas(T("List growth levers backed by data.", "Listar alavancas de crescimento sustentadas por dados."), string.Join(" | ", ops.Select(o => o.Titulo)),
            string.Join(" | ", ops.Select(o => o.Evidencia)), T("Each lever is an opportunity, not a commitment.", "Cada alavanca é uma oportunidade, não um compromisso."), string.Join(" | ", ops.Select(o => o.Acao)),
            T("Simulations apply the stated rate to the stated base; they are not forecasts.", "Simulações aplicam a taxa indicada à base indicada; não são previsões."));
        return s;
    }

    private AxSlide? Cenarios()
    {
        var ip = IP(R);
        var baseV = _o.BaseTodas ? ip.VTotal : ip.VG + ip.VP;
        var taxa = ip.TaxaValor;
        if (baseV <= 0 || taxa is null) { Omitir("cenarios", T("Improvement Scenarios", "Cenários de melhoria"), Indisp); return null; }
        var baseQ = _o.BaseTodas ? ip.Linhas : ip.Decididas;
        var s = Novo("cenarios", T("Improvement Scenarios | What if conversion were higher?", "Cenários de melhoria | E se a conversão fosse maior?"),
            T($"SIMULATION, not a forecast · eligible base: {Din(Mo(baseV, R))} {(_o.BaseTodas ? "quoted" : "decided (won + lost)")} in {PerR}", $"SIMULAÇÃO, não previsão · base elegível: {Din(Mo(baseV, R))} {(_o.BaseTodas ? "ofertados" : "decididos (ganho + perdido)")} em {PerR}"));
        var pps = new[] { 0, 1, 3, 5 };
        var cats = pps.Select(p => p == 0 ? T("Current", "Atual") : $"+{p} p.p.").ToArray();
        var ch = new AxChart
        {
            Tipo = AxTipoGrafico.Colunas, Categorias = cats, Formato = FmtMi, Legenda = false, Espaco = 70,
            Series = { new AxSerie(T("Won value", "Valor ganho"), pps.Select(p => MoMi(ip.VG + baseV * p / 100, R)).ToArray(), AxCores.VerdeMedio) { CoresPontos = pps.Select(p => p == 0 ? AxCores.Neutro : null).ToArray() } },
            Descricao = T("Won value by scenario", "Valor ganho por cenário"),
        };
        long wg = 5303520;
        Grafico(s, XL, Y0, wg, YB - Y0, T($"Won value by scenario · {PerR} ({Sim} millions)", $"Valor ganho por cenário · {PerR} ({Sim} milhões)"), ch);
        var linhas = new List<AxCelula[]> { new[] { Cab(T("Scenario", "Cenário"), "l"), Cab(T("Conv. value", "Conv. valor"), "r"), Cab(T("Won value", "Valor ganho"), "r"), Cab(T("Increment", "Incremento"), "r"), Cab(T("Extra wins*", "Ganhas a mais*"), "r") } };
        foreach (var p in pps)
            linhas.Add(new[] { Rot(cats[Array.IndexOf(pps, p)], p == 0), Num(Pct(taxa + p)), Num(Din(Mo(ip.VG + baseV * p / 100, R))), Num(p == 0 ? "—" : "+" + Din(Mo(baseV * p / 100, R)), p == 0 ? null : AxCores.VerdeMedio, true), Num(p == 0 ? "—" : "+" + N1(baseQ * p / 100.0)) });
        var xt = XL + wg + 182880;
        s.Elems.Add(new AxTabela(xt, Y0, XR - xt, 393192L * linhas.Count) { Pesos = new[] { 1.3, 1.1, 1.3, 1.3, 1.1 }, Linhas = linhas, Tam = 1000, Nome = "Scenarios table" });
        var nota = T($"Increment = eligible base × p.p. · *Extra wins = {N(baseQ)} {(_o.BaseTodas ? "proposals" : "decided proposals")} × p.p. (quantity view). Same base and period for all scenarios; no new proposals assumed.",
                     $"Incremento = base elegível × p.p. · *Ganhas a mais = {N(baseQ)} {(_o.BaseTodas ? "propostas" : "propostas decididas")} × p.p. (visão por quantidade). Mesma base e período em todos os cenários; nenhuma proposta nova presumida.");
        s.Elems.Add(Txt(xt, Y0 + 393192L * linhas.Count + 137160, XR - xt, 731520, new AxRun(nota, AxMedida.Caber(nota, XR - xt, 731520, 1000, 750), false, AxCores.Texto, true)));
        var txt = T($"Each +1 p.p. of value conversion on the {PerR} base adds ≈ {Din(Mo(baseV / 100, R))} of won value.", $"Cada +1 p.p. de conversão por valor na base de {PerR} soma ≈ {Din(Mo(baseV / 100, R))} em valor ganho.");
        Barra(s, T("SIMULATION", "SIMULAÇÃO"), txt);
        s.Notas = Notas(T("Size the value of conversion improvements.", "Dimensionar o valor de melhorar a conversão."), txt,
            T($"Current value conversion {Pct(taxa)} on {Din(Mo(baseV, R))}.", $"Conversão por valor atual {Pct(taxa)} sobre {Din(Mo(baseV, R))}."),
            T("Small conversion gains on a large base are worth more than extra quoting volume.", "Pequenos ganhos de conversão numa base grande valem mais que volume extra de oferta."),
            T("Pick the scenario the team commits to and track it monthly.", "Escolher o cenário com que a equipe se compromete e acompanhar mensalmente."),
            T("These are simulations: they do not predict future results.", "São simulações: não preveem resultados futuros."));
        return s;
    }

    private AxSlide? PlanoDeAcao()
    {
        var ops = Oportunidades_();
        var acoes = ops.Select(o => (o.Titulo, o.Acao, o.Indicador)).ToList();
        if (!Escopado && _m.Metas.TryGetValue(R, out var mt) && Mo(mt.Hsa, R) is decimal meta && Mo(Ven(R, 1, UltV), R) is decimal v && meta > v && UltV < 12)
            acoes.Insert(0, (T("Close the target gap", "Fechar o gap da meta"), T($"Plan {Din((meta - v) / (12 - UltV))}/month for the remaining {12 - UltV} months using the open pipeline.", $"Planejar {Din((meta - v) / (12 - UltV))}/mês nos {12 - UltV} meses restantes com o pipeline aberto."),
                T("Monthly sales vs required run-rate", "Vendas do mês × ritmo necessário")));
        if (acoes.Count == 0) { Omitir("plano", T("Action Plan", "Plano de ação"), Indisp); return null; }
        var s = Novo("plano", T("Action Plan | Next steps", "Plano de ação | Próximos passos"),
            T("Actions derived from the findings · owners and dates to be agreed by leadership", "Ações derivadas das constatações · responsáveis e prazos a definir pela liderança"));
        var linhas = new List<AxCelula[]> { new[] { Cab("#", "ctr"), Cab(T("Priority", "Prioridade"), "l"), Cab(T("Action", "Ação"), "l"), Cab(T("Indicator to track", "Indicador de acompanhamento"), "l"), Cab(T("Owner", "Responsável"), "l"), Cab(T("Due date", "Prazo"), "l") } };
        var k = 1;
        foreach (var (titulo, acao, ind) in acoes.Take(5))
            linhas.Add(new[] { new AxCelula((k++).ToString("00"), true, AxCores.Verde, null, "ctr"), Rot(titulo, true), Rot(acao), Rot(ind), new AxCelula(T("To be assigned", "A definir"), false, AxCores.Cinza), new AxCelula(T("To be defined", "A definir"), false, AxCores.Cinza) });
        var hl = Math.Min(777240L, (YB - Y0 - 320040) / Math.Max(1, linhas.Count - 1));
        s.Elems.Add(new AxTabela(XL, Y0, XR - XL, 320040 + hl * (linhas.Count - 1)) { Pesos = new[] { 0.45, 1.9, 3.2, 2.0, 1.1, 1.0 }, Linhas = linhas, Tam = linhas.Count > 5 ? 900 : 1000, Nome = "Action plan table" });
        var txt = T("Owners and due dates are intentionally left open: they must be agreed in the meeting, not assumed by the system.", "Responsáveis e prazos ficam em aberto de propósito: devem ser acordados na reunião, não presumidos pelo sistema.");
        Barra(s, T("DECISION", "DECISÃO"), txt);
        s.Notas = Notas(T("Turn findings into accountable actions.", "Transformar constatações em ações com dono."), string.Join(" | ", acoes.Select(a => a.Item1)),
            T("Each action comes from a data point shown earlier in the deck.", "Cada ação vem de um dado mostrado antes na apresentação."),
            T("Without owners and dates, actions do not happen.", "Sem dono e prazo, as ações não acontecem."), txt);
        return s;
    }

    private AxSlide Conclusoes()
    {
        var ip = IP(R); var ic = IP(C);
        var vr = Mo(VP(R), R); var vc = Mo(VP(C), C);
        var s = Novo("conclusoes", T("Executive Conclusions | What the numbers say", "Conclusões executivas | O que os números dizem"),
            T("Fact → evidence → implication → action · no causes are inferred", "Fato → evidência → implicação → ação · nenhuma causa é presumida"));
        var r1 = !Escopado && vr is not null
            ? T($"Sales {Din(vr)} ({PerVendasR}){(Cmp && vc is not null ? $", {Var(VarPct(vr, vc))} vs {C}" : "")} → {(vr >= (vc ?? 0) ? "ahead of" : "behind")} last year on the same months → keep the monthly run-rate on the target.",
                $"Vendas de {Din(vr)} ({PerVendasR}){(Cmp && vc is not null ? $", {Var(VarPct(vr, vc))} vs {C}" : "")} → {(vr >= (vc ?? 0) ? "à frente" : "atrás")} do ano anterior nos mesmos meses → manter o ritmo mensal na meta.")
            : T($"Won value {Din(Mo(ip.VG, R))} in {PerR} → result of the period for this scope → compare with the open pipeline.", $"Valor ganho de {Din(Mo(ip.VG, R))} em {PerR} → resultado do período neste recorte → comparar com o pipeline aberto.");
        var r2 = T($"Conversion {Pct(ip.TaxaQtd)} (qty) / {Pct(ip.TaxaValor)} (value){(Cmp ? $", {Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))} vs {C}" : "")} → each +1 p.p. in value ≈ {Din(Mo((_o.BaseTodas ? ip.VTotal : ip.VG + ip.VP) / 100, R))} → focus on qualification and follow-up.",
                   $"Conversão de {Pct(ip.TaxaQtd)} (qtd) / {Pct(ip.TaxaValor)} (valor){(Cmp ? $", {Pp(Dif(ip.TaxaQtd, ic.TaxaQtd))} vs {C}" : "")} → cada +1 p.p. em valor ≈ {Din(Mo((_o.BaseTodas ? ip.VTotal : ip.VG + ip.VP) / 100, R))} → foco em qualificação e follow-up.");
        var bus = _o.Escopo == "bu" ? null : _m.Dim["bu"].Chaves.Select(k => (k, i: DimP("bu", k, R))).Where(x => x.i.VG > 0).OrderByDescending(x => x.i.VG).ToList();
        var totBu = bus?.Sum(x => x.i.VG) ?? 0;
        var r3 = bus is { Count: > 0 } && totBu > 0
            ? T($"{bus[0].k} holds {Pct(bus[0].i.VG / totBu * 100)} of the won value → results depend on few units → protect the leaders and develop the next ones.", $"{bus[0].k} detém {Pct(bus[0].i.VG / totBu * 100)} do valor ganho → o resultado depende de poucas unidades → proteger as líderes e desenvolver as próximas.")
            : T($"Quoted value {Din(Mo(ip.ValorEnviado, R))} in {N(ip.Enviadas)} proposals → mix defines the result → see BU, segment and product pages.", $"Valor ofertado de {Din(Mo(ip.ValorEnviado, R))} em {N(ip.Enviadas)} propostas → o mix define o resultado → ver BU, segmento e produto.");
        var r4 = T($"{Din(Mo(ip.VA, R))} still open and {Din(Mo(ip.VP, R))} lost in {PerR} → value at stake is in execution → run the action plan with owners and dates.",
                   $"{Din(Mo(ip.VA, R))} ainda em aberto e {Din(Mo(ip.VP, R))} perdidos em {PerR} → o valor em jogo está na execução → executar o plano de ação com donos e prazos.");
        long h = 822960, g = 91440;
        LinhaNumerada(s, Y0, h, "01", T("RESULT", "RESULTADO"), r1);
        LinhaNumerada(s, Y0 + (h + g), h, "02", T("EFFICIENCY", "EFICIÊNCIA"), r2);
        LinhaNumerada(s, Y0 + 2 * (h + g), h, "03", T("MIX", "MIX"), r3);
        LinhaNumerada(s, Y0 + 3 * (h + g), h, "04", T("EXECUTION", "EXECUÇÃO"), r4);
        var txt = T("Validate the figures against the dashboard before external use. Confidential.", "Validar os números com o painel antes de qualquer uso externo. Confidencial.");
        Barra(s, T("FINAL CHECK", "VALIDAÇÃO FINAL"), txt);
        s.Notas = string.Join("\n", new[] { T("PURPOSE: close with the four messages leadership should retain (fact → implication → action).", "OBJETIVO: fechar com as quatro mensagens que a liderança deve reter (fato → implicação → ação)."),
            "01 " + r1, "02 " + r2, "03 " + r3, "04 " + r4, Regra, Fonte, T("Confidential – aggregated data only.", "Confidencial – somente dados agregados.") });
        return s;
    }

    private AxSlide Definicoes()
    {
        var s = Novo("definicoes", T("Appendix | Definitions and methodology", "Apêndice | Definições e metodologia"),
            T("The rules that keep comparisons correct", "As regras que garantem comparações corretas"), T("APPENDIX", "APÊNDICE"));
        var itens = new[]
        {
            (T("Sales", "Vendas"), T("Team sales history, company-wide, by billing month (recognition). Negative months are kept as recorded.", "Histórico de vendas da equipe, empresa toda, por mês de faturamento (reconhecimento). Meses negativos ficam como lançados.")),
            (T("Proposals sent", "Propostas enviadas"), T("By send date; each proposal number counted once (earliest send, highest value). Manually informed months apply when there is no filter.", "Pela data de envio; cada número de proposta conta uma vez (envio mais antigo, maior valor). Meses informados à mão valem sem filtro.")),
            (T("Conversion (qty)", "Conversão (qtd)"), _o.BaseTodas ? T("Won ÷ all proposals sent.", "Ganhas ÷ todas as enviadas.") : T("Won ÷ (won + lost). Open proposals excluded. Cancelled count as lost.", "Ganhas ÷ (ganhas + perdidas). Abertas fora. Canceladas contam como perdidas.")),
            (T("Conversion (value)", "Conversão (valor)"), _o.BaseTodas ? T("R$ won ÷ R$ of all proposals sent.", "R$ ganho ÷ R$ de todas as enviadas.") : T("R$ won ÷ (R$ won + R$ lost).", "R$ ganho ÷ (R$ ganho + R$ perdido).")),
            (T("Status and cohort", "Status e coorte"), T("Current status at extraction for proposals grouped by send month; recent cohorts still change.", "Status atual na extração, para propostas agrupadas pelo mês de envio; coortes recentes ainda mudam.")),
            (T("Period and cut-off", "Período e corte"), T($"{PerR} vs {PerC}; all years compared on the same months; cut-off {Data(Corte)}.", $"{PerR} × {PerC}; todos os anos comparados nos mesmos meses; corte {Data(Corte)}.")),
            (T("Currency and targets", "Moeda e metas"), (_o.Usd ? T("USD at the annual rate registered in the app. ", "US$ pela taxa anual cadastrada. ") : T("BRL; AFM USD values converted at the annual rate. ", "R$; valores AFM em US$ convertidos pela taxa anual. ")) + T("Targets: annual HSA and Group budgets.", "Metas: budgets anuais HSA e Grupo.")),
        };
        // ajuste manual de ganhas (ainda não lançadas no CRM): entra como uma linha a mais das regras
        var aj = _fonte.Propostas.Where(p => p.AjusteManual && p.Data is not null)
            .GroupBy(p => (p.Origem, p.Data!.Value.Year, p.Data.Value.Month)).OrderBy(g => g.Key).ToList();
        if (aj.Count > 0)
        {
            var lista = string.Join(", ", aj.Select(g => $"{g.Count()} {g.Key.Origem} {g.Key.Month:00}/{g.Key.Year}"));
            itens = itens.Append((T("Manual adjustment", "Ajuste manual"),
                T($"Won proposals not yet entered in the CRM, counted as won: {lista}. Count conversion only; in value they remain open.",
                  $"Propostas ganhas ainda não lançadas no CRM, contadas como ganhas: {lista}. Só na conversão por quantidade; no valor continuam em aberto."))).ToArray();
        }
        long h = 512064, g = 45720, y = Y0;
        var k = 0;
        foreach (var (rot, txt) in itens)
        {
            s.Elems.Add(new AxForma(676656, y, 10835640, h) { Geo = "roundRect", Fundo = k++ % 2 == 0 ? AxCores.CardLinha : "FFFFFF", Borda = "E0ECE7" });
            s.Elems.Add(Txt(822960, y, 2743200, h, new AxRun(rot, 1200, true, AxCores.Escuro), "l", "ctr"));
            s.Elems.Add(Txt(3611880, y + 27432, 7772400, h - 54864, new AxRun(txt, AxMedida.Caber(txt, 7772400, h - 54864, 1100, 800), false, AxCores.Texto), "l", "ctr"));
            y += h + g;
        }
        s.Notas = T("Definitions used in every slide. ", "Definições usadas em todos os slides. ") + string.Join(" | ", itens.Select(i => i.Item1 + ": " + i.Item2)) + "\n" + Fonte;
        return s;
    }

    private AxSlide Limitacoes()
    {
        var s = Novo("limitacoes", T("Appendix | Data limitations and omitted sections", "Apêndice | Limitações dos dados e seções omitidas"),
            T("What this deck cannot show, and why", "O que esta apresentação não mostra, e por quê"), T("APPENDIX", "APÊNDICE"));
        var lim = new List<string>
        {
            T("There is no decision (close) date for proposals: status is the current one at extraction; conversion of recent months can still change.", "Não há data de decisão (fechamento) das propostas: o status é o atual na extração; a conversão dos meses recentes ainda pode mudar."),
            T("Sales are recorded company-wide only (no sales by BU, country, sector, segment, product or salesperson): those views use won value.", "As vendas só existem para a empresa toda (não por BU, país, setor, segmento, produto ou vendedor): essas visões usam o valor ganho."),
            T("Loss reasons are not recorded in the source data; none are inferred.", "Os motivos de perda não estão na base; nenhum é presumido."),
            T("Targets are annual (HSA and Group); there are no monthly, BU or sector targets.", "As metas são anuais (HSA e Grupo); não há metas mensais, por BU ou por setor."),
        };
        if (UltV < Mr && !Escopado) lim.Add(T($"Sales of {R} are recorded up to {(UltV > 0 ? Mn(UltV) : "—")}: sales comparisons use the same months.", $"As vendas de {R} estão lançadas até {(UltV > 0 ? Mn(UltV) : "—")}: as comparações de vendas usam os mesmos meses."));
        if (_inicio.Count >= 2 && _inicio.Values.Distinct().Count() > 1)
            lim.Add(T($"Proposal history starts at different times ({string.Join(", ", _inicio.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} from {kv.Value}"))}): proposal charts start in {_anoComparavel} so all years compare like with like.",
                      $"O histórico de propostas começa em anos diferentes ({string.Join(", ", _inicio.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} desde {kv.Value}"))}): os gráficos de propostas começam em {_anoComparavel} para comparar igual com igual."));
        else if (AnosP.First() > Anos.First()) lim.Add(T($"Proposal data is recorded from {AnosP.First()}: proposal-based charts start in that year.", $"Os dados de propostas existem a partir de {AnosP.First()}: os gráficos de propostas começam nesse ano."));
        if (_o.Usd) lim.Add(T("USD values use one annual exchange rate per year.", "Os valores em US$ usam uma taxa de câmbio anual por ano."));
        var omit = _omitidos.Select(o => $"{o.Titulo}: {o.Motivo}").ToList();
        var pars = new List<AxPar> { new(new AxRun(T("DATA LIMITATIONS", "LIMITAÇÕES DOS DADOS"), 1000, true, AxCores.VerdeMedio), "l", 200) };
        pars.AddRange(lim.Select(l => new AxPar(new AxRun("•  " + l, 1050, false, AxCores.Texto), "l", 250)));
        pars.Add(new AxPar(new AxRun(T("OMITTED SECTIONS", "SEÇÕES OMITIDAS"), 1000, true, AxCores.VerdeMedio), "l", 200));
        pars.AddRange(omit.Count == 0 ? new[] { new AxPar(new AxRun(T("None: all sections had data.", "Nenhuma: todas as seções tinham dados."), 1050, false, AxCores.Texto), "l", 250) }
                                      : omit.Select(o => new AxPar(new AxRun("•  " + o, 1050, false, AxCores.Texto), "l", 250)));
        var todo = string.Join("\n", pars.Select(p => p.TextoPuro));
        var tam = AxMedida.Caber(todo, 10469880, YB - Y0 + 274320, 1300, 750, 1.5);
        var pars2 = pars.Select(p => p with { Runs = p.Runs.Select(r => r with { Tam = r.Negrito ? Math.Min(1100, tam) : tam }).ToList() }).ToList();
        s.Elems.Add(new AxForma(676656, Y0, 10835640, YB - Y0 + 548640) { Geo = "roundRect", Arredondado = 3000, Fundo = AxCores.CardLinha, Borda = "E0ECE7", MargemH = 182880, MargemV = 137160, Pars = pars2 });
        s.Notas = T("Limitations and omitted sections. ", "Limitações e seções omitidas. ") + string.Join(" | ", lim.Concat(omit));
        return s;
    }
}
