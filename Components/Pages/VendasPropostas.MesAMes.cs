using System.Globalization;
using HowdenSalesForecast.Data;

namespace HowdenSalesForecast.Components.Pages;

// ---------------------------------------------------------------------------
// Seção "Vendas e propostas, mês a mês" da guia Performance Comercial: os
// cálculos (a marcação fica em VendasPropostas.razor). Três modos:
//   "ano"       um ano: vendas e propostas de cada mês;
//   "comparar"  dois anos lado a lado, com a variação de cada mês e o resumo
//               do período comparável (só os meses fechados nos dois anos);
//   "historico" todos os anos numa matriz ano × mês.
// Regras: mês sem dado nunca vira zero ("n/d"); mês que ainda não fechou fica
// "a apurar"; o mês corrente das propostas é "parcial" e fica fora das
// comparações. A taxa de conversão é a da página (Taxa / TaxaMes), sem fórmula nova.
// ---------------------------------------------------------------------------
public partial class VendasPropostas
{
    /// <summary>O estado de cada quadro do mês a mês (modo, ano comparado, dados detalhados): cada um tem o seu.</summary>
    private sealed class EstadoMm
    {
        public string Modo = "comparar";
        public int? Comp;
        public bool Detalhe;
    }
    private readonly EstadoMm _mmEmpresa = new(), _mmSetores = new();
    /// <summary>
    /// Qual quadro está sendo desenhado: false = "Vendas e propostas, mês a mês" (vendas da empresa toda);
    /// true = "Vendas por setor" (vendas NB e AFM, que seguem o filtro de Setor, e as duas conversões, quantidade e valor).
    /// Ligado só durante o desenho do quadro (SecaoMesAMes); os cliques usam o estado capturado.
    /// </summary>
    private bool _mmS;
    private EstadoMm Mm => _mmS ? _mmSetores : _mmEmpresa;

    /// <summary>Taxa de conversão do mês no quadro: por quantidade ou por valor (botões da barra de filtros); no quadro por setor, sempre por quantidade (a de valor tem a sua linha).</summary>
    private double? TaxaMm(int ano, int mes) => _mmS ? TaxaQtdMes(ano, mes) : TaxaMes(ano, mes).Taxa;

    /// <summary>Por quantidade: ganhas ÷ todas as enviadas no mês, ou ÷ decididas (botão "Taxa sobre").</summary>
    private double? TaxaQtdMes(int ano, int mes)
    {
        var l = _convSetor.Where(x => x.Ano == ano && x.Mes == mes).ToList();
        var g = l.Count(x => x.Classe == ClasseProposta.Ganha);
        var b = BaseTodas ? l.Count : g + l.Count(x => x.Classe == ClasseProposta.Perdida);
        return b > 0 ? g * 100.0 / b : null;
    }

    // ---- vendas por setor (NB e AFM) ----

    private Dictionary<(int Ano, int Mes), VendaPorSetor> _vSetor = new();
    private bool VeNb => _fil.Origens.Count == 0 || _fil.Origens.Contains(ConversaoService.Nb);
    private bool VeAfm => _fil.Origens.Count == 0 || _fil.Origens.Contains(ConversaoService.Afm);

    /// <summary>Vendas do mês pelo filtro de Setor: só NB, só AFM ou as duas somadas (sem filtro). null = falta o número.</summary>
    private decimal? VendaSetor(int a, int m)
    {
        if (!_vSetor.TryGetValue((a, m), out var r) || (!VeNb && !VeAfm)) return null;
        if ((VeNb && r.Nb is null) || (VeAfm && r.Afm is null)) return null;
        return (VeNb ? r.Nb ?? 0m : 0m) + (VeAfm ? r.Afm ?? 0m : 0m);
    }

    /// <summary>De que vendas o quadro fala: "empresa toda", "NB + AFM", "só NB", "só AFM".</summary>
    private string VendasDeMm => !_mmS ? T("empresa toda", "whole company") : VeNb && VeAfm ? "NB + AFM" : VeNb ? T("só NB", "NB only") : VeAfm ? T("só AFM", "AFM only") : T("nenhum setor", "no sector");

    /// <summary>Por valor: valor ganho ÷ (ganho + perdido), ou ÷ valor de todas as enviadas (mesma regra da guia Conversão).</summary>
    private double? TaxaValorMes(int ano, int mes)
    {
        var l = _convSetor.Where(x => x.Ano == ano && x.Mes == mes).ToList();
        double vg = l.Where(x => x.ClasseValor == ClasseProposta.Ganha).Sum(x => x.Brl ?? 0);
        double vp = l.Where(x => x.Classe == ClasseProposta.Perdida).Sum(x => x.Brl ?? 0);
        var vb = BaseTodas ? l.Sum(x => x.Brl ?? 0) : vg + vp;
        return vb > 0 ? vg / vb * 100 : null;
    }
    /// <summary>Qual paleta de cores está aberta: "u", "c", "t" (o modo) ou nenhuma.</summary>
    private string? _mmPaleta;

    // ---- cores do mês a mês: cada modo tem as suas ----
    // Chave gravada: "m" + modo (u/c/t) + elemento + ano. Elementos: v = colunas de vendas, p = colunas de
    // propostas, e = linha do valor enviado, x = linha da conversão, n = venda negativa (sem ano).

    private static string ChaveMm(string modo, char el, int ano) => $"m{modo}{el}{(ano > 0 ? ano.ToString() : "")}";

    private string NomeElMm(char el) => el switch
    {
        'v' => T("colunas de vendas", "sales columns"), 'p' => T("colunas de propostas", "proposal columns"), 'e' => T("linha do valor enviado", "value sent line"),
        'x' => T("linha da conversão", "conversion line"), _ => T("venda negativa", "negative sales"),
    };

    /// <summary>A cor do elemento no modo: a escolhida; senão o padrão (derivado da cor do ano).</summary>
    private string CorMm(string modo, char el, int ano)
    {
        if (CorSalva(ChaveMm(modo, el, ano)) is { } c) return c;
        return el switch
        {
            'v' => CorDoAno(ano),
            'p' => Clarear(CorMm(modo, 'v', ano), 0.5),
            'e' => modo == "t" ? "#64748b" : CorMm(modo, 'v', ano),
            'x' => modo == "t" ? "#b45309" : CorMm(modo, 'v', ano),
            _ => "#c0392b",
        };
    }

    /// <summary>Mistura a cor com branco (f = quanto da cor fica): "#12355b", 0,5 → um tom mais claro.</summary>
    private static string Clarear(string hex, double f)
    {
        if (hex.Length != 7 || hex[0] != '#') return hex;
        int C(int i) => (int)Math.Round(Convert.ToInt32(hex.Substring(i, 2), 16) * f + 255 * (1 - f));
        return $"#{C(1):x2}{C(3):x2}{C(5):x2}";
    }

    /// <summary>Primeiro mês (ano × 100 + mês) com propostas nas planilhas, sem filtros: antes dele, "n/d".</summary>
    private int _mmIniPropostas = int.MaxValue;

    private enum SitMes { Valor, SemDado, AApurar }

    /// <summary>Um mês de um ano: o valor (vendas em R$ ou quantidade de propostas), ou por que não há valor.</summary>
    private readonly record struct CelMm(SitMes Sit, decimal Valor, bool Parcial)
    {
        public bool Tem => Sit == SitMes.Valor;
    }

    private static readonly CelMm SemDadoMm = new(SitMes.SemDado, 0m, false);
    private static readonly CelMm AApurarMm = new(SitMes.AApurar, 0m, false);

    /// <summary>Vendas do mês. No ano corrente, mês sem venda registrada ainda está "a apurar"; nos anteriores, "n/d".</summary>
    private CelMm CelVenda(int a, int m)
    {
        var venda = _mmS ? VendaSetor(a, m) : _meses.FirstOrDefault(x => x.Ano == a && x.Mes == m)?.Vendas;
        if (venda is decimal v) return new(SitMes.Valor, v, false);
        return a >= DateTime.Today.Year ? AApurarMm : SemDadoMm;
    }

    /// <summary>
    /// Propostas enviadas no mês. Meses futuros: "a apurar". Antes do início das planilhas: "n/d".
    /// Depois disso, mês sem proposta é zero de verdade. O mês corrente é parcial.
    /// </summary>
    private CelMm CelProp(int a, int m)
    {
        var hoje = DateTime.Today;
        if (a > hoje.Year || (a == hoje.Year && m > hoje.Month)) return AApurarMm;
        var p = Prop(a, m);
        if (p is null && a * 100 + m < _mmIniPropostas) return SemDadoMm;
        return new(SitMes.Valor, p?.Qtd ?? 0, a == hoje.Year && m == hoje.Month);
    }

    private CelMm Cel(bool vendas, int a, int m) => vendas ? CelVenda(a, m) : CelProp(a, m);

    /// <summary>Último mês fechado de vendas: dezembro nos anos passados; no corrente, o último com venda registrada.</summary>
    private int FechadoV(int a)
    {
        var hoje = DateTime.Today;
        if (a < hoje.Year) return 12;
        if (a > hoje.Year) return 0;
        return Enumerable.Range(1, 12).LastOrDefault(m => CelVenda(a, m).Tem);
    }

    /// <summary>Último mês fechado de propostas: dezembro nos anos passados; no corrente, o mês anterior ao de hoje.</summary>
    private static int FechadoP(int a)
    {
        // até o mês presente: o mês em andamento entra (parcial, até hoje), como no topo da página
        var hoje = DateTime.Today;
        return a < hoje.Year ? 12 : a > hoje.Year ? 0 : hoje.Month;
    }

    /// <summary>Soma dos meses 1..ate que têm valor, e quantos meses entraram.</summary>
    private (decimal Soma, int Meses) SomaMm(bool vendas, int a, int ate)
    {
        decimal s = 0; var n = 0;
        for (var m = 1; m <= ate; m++)
        {
            if (!MesNoFiltro(m)) continue;   // filtro "Mês": a comparação usa só os meses escolhidos
            var c = Cel(vendas, a, m);
            if (!c.Tem) continue;
            s += c.Valor; n++;
        }
        return (s, n);
    }

    /// <summary>O ano comparado: o escolhido; senão o anterior ao principal; senão o mais próximo.</summary>
    private int? CompMm(int principal)
    {
        var anos = AnosDoPainel.Where(a => a != principal).ToList();
        if (anos.Count == 0) return null;
        if (Mm.Comp is int c && anos.Contains(c)) return c;
        if (anos.Contains(principal - 1)) return principal - 1;
        return anos.OrderBy(a => Math.Abs(a - principal)).ThenByDescending(a => a).First();
    }

    /// <param name="Taxa">a conversão do quadro (no quadro por setor, por quantidade)</param>
    /// <param name="TaxaV">a conversão por valor (R$ ganho ÷ R$ da base)</param>
    private sealed record LadoMm(int Ano, decimal Vendas, int MesesV, int Propostas, int MesesP, double? Taxa, int G, int Base, double? TaxaV);

    /// <summary>
    /// Resumo de dois anos no período comparável: vendas de jan até o último mês fechado nos dois;
    /// propostas e conversão de jan até o mês presente nos dois (o mês em andamento entra, parcial).
    /// </summary>
    private (int Mv, int Mp, LadoMm A, LadoMm B) ResumoMm(int a1, int a2)
    {
        var mv = Math.Min(FechadoV(a1), FechadoV(a2));
        var mp = Math.Min(FechadoP(a1), FechadoP(a2));
        LadoMm Lado(int a)
        {
            var (v, nv) = SomaMm(true, a, mv);
            var (p, np) = SomaMm(false, a, mp);
            var t = mp > 0 ? Taxa(a, mp) : default;
            return new LadoMm(a, v, nv, (int)p, np, mp > 0 ? (_mmS ? t.TaxaQtd : t.Taxa) : null, t.G, t.Base, mp > 0 ? t.TaxaValor : null);
        }
        return (mv, mp, Lado(a1), Lado(a2));
    }

    // ---- escala das barras (em % da altura do gráfico), com o zero no lugar certo ----

    private sealed record EscalaMm(double Min, double Max)
    {
        private double Faixa => Max - Min;
        /// <summary>Altura da linha do zero, em % a partir de baixo.</summary>
        public double Zero => -Min / Faixa * 100;
        /// <summary>Onde a barra começa (em %): no zero para valores positivos; abaixo dele para negativos.</summary>
        public double Base(double v) => v >= 0 ? Zero : Zero - Alt(v);
        public double Alt(double v) => Math.Abs(v) / Faixa * 100;
        public double Topo(double v) => v >= 0 ? Zero + Alt(v) : Zero;
    }

    /// <summary>Escala que cabe todos os valores, com folga em cima (rótulos) e embaixo (barras negativas e seus rótulos).</summary>
    private static EscalaMm EscalaMmDe(IEnumerable<double> valores, double folgaTopo = 0.17)
    {
        var l = valores.ToList();
        double max = Math.Max(0, l.DefaultIfEmpty(0).Max()), min = Math.Min(0, l.DefaultIfEmpty(0).Min());
        if (max <= 0 && min >= 0) max = 1;
        var faixa = max - min;
        max += faixa * folgaTopo;
        if (min < 0) min -= faixa * (folgaTopo > 0.2 ? 0.3 : 0.16);
        return new EscalaMm(min, max);
    }

    // ---- textos ----

    /// <summary>Cultura dos números do mês a mês: pt-BR, ou en-US com a tela em inglês.</summary>
    private CultureInfo CulMm => En ? EnUs : Br;

    /// <summary>R$ em milhões com uma casa, sem a unidade (o título do gráfico diz "R$ milhões"): "31,4", "−6,1".</summary>
    private string MiCurto(decimal v) => (v < 0 ? "−" : "") + (Math.Abs(v) / 1_000_000m).ToString("#,##0.0", CulMm);

    /// <summary>Valor exato, com centavos: "R$ 31.412.345,67".</summary>
    private string Reais(decimal v) => (v < 0 ? "−" : "") + Sim + " " + Math.Abs(v).ToString("#,##0.00", CulMm);

    /// <summary>O mês está no filtro "Mês" da barra de filtros (sem filtro, todos estão).</summary>
    private bool MesNoFiltro(int m) => _fil.Meses.Count == 0 || _fil.Meses.Contains(Fmt.MonthLong(m));

    /// <summary>Os meses de jan até "ate" que estão no filtro, em faixas: "jan–mar", "jan–mar, jun", "jan–dez".</summary>
    private string MesesTxt(int ate)
    {
        if (ate <= 0) return T("sem meses fechados", "no closed months");
        var l = Enumerable.Range(1, Math.Min(12, ate)).Where(MesNoFiltro).ToList();
        if (l.Count == 0) return T("nenhum mês do filtro", "no month in the filter");
        var faixas = new List<string>();
        for (var i = 0; i < l.Count;)
        {
            var j = i;
            while (j + 1 < l.Count && l[j + 1] == l[j] + 1) j++;
            var de = MesMm(l[i]); var ate2 = MesMm(l[j]);
            faixas.Add(i == j ? de : $"{de}–{ate2}");
            i = j + 1;
        }
        return string.Join(", ", faixas);
    }

    /// <summary>De janeiro até "ate", sem o filtro "Mês": "jan–dez", "jan–out".</summary>
    private string MesesTxtSemFiltro(int ate) => ate <= 1 ? MesMm(1) : $"{MesMm(1)}–{MesMm(Math.Min(12, ate))}";

    /// <summary>Mês abreviado das faixas: minúsculo em português ("jan"), com inicial maiúscula em inglês ("Jan").</summary>
    private string MesMm(int m) => En ? MesCurto(m) : MesCurto(m).ToLowerInvariant();

    /// <summary>Variação % (null quando a base é zero ou negativa: a conta não tem sentido).</summary>
    private static double? VarMm(decimal atual, decimal baseAnt) => baseAnt > 0 ? (double)((atual - baseAnt) / baseAnt * 100) : null;

    private string PctSinal(double v) => (v > 0.05 ? "+" : v < -0.05 ? "−" : "") + Math.Abs(v).ToString("0.0", CulMm) + "%";
    private string PpSinal(double v) => (v > 0.05 ? "+" : v < -0.05 ? "−" : "") + Math.Abs(v).ToString("0.0", CulMm) + T(" p.p.", " pp");
    private string DifReais(decimal d) => (d > 0 ? "+" : d < 0 ? "−" : "") + Sim + " " + Compacto(Math.Abs(d));
    private string DifQtd(int d) => (d > 0 ? "+" : d < 0 ? "−" : "") + Math.Abs(d).ToString("#,##0", CulMm);

    /// <summary>Classe de cor da variação: vendas e conversão sobem = bom; propostas são neutras (mais propostas não é, por si, melhor).</summary>
    private static string ClsVar(double? v, bool neutra) => v is not double x || Math.Abs(x) < 0.05 ? "neu" : neutra ? "neu" : x > 0 ? "pos" : "neg";
    private static string Seta(double? v) => v is not double x || Math.Abs(x) < 0.05 ? "" : x > 0 ? "▲ " : "▼ ";

    private string CelTxt(bool vendas, CelMm c, bool exato) => c.Sit switch
    {
        SitMes.SemDado => T("n/d", "n/a"),
        SitMes.AApurar => T("a apurar", "pending"),
        _ => vendas ? (exato ? Reais(c.Valor) : MiCurto(c.Valor)) : N0((int)c.Valor) + (c.Parcial && exato ? T(" (parcial)", " (partial)") : ""),
    };

    /// <summary>Linha de situação dos dados, no topo da seção.</summary>
    private string StatusMm()
    {
        var hoje = DateTime.Today;
        var ultV = _mmS
            ? _vSetor.Keys.Where(k => VendaSetor(k.Ano, k.Mes) is not null).Select(k => (k.Ano, k.Mes)).OrderBy(k => k.Ano * 100 + k.Mes).Select(k => ((int, int)?)k).LastOrDefault()
            : _meses.Where(m => m.Vendas is not null).OrderBy(m => m.Ano * 100 + m.Mes).Select(m => ((int, int)?)(m.Ano, m.Mes)).LastOrDefault();
        var partes = new List<string>
        {
            ultV is not (int ua, int um) ? T("Vendas: nenhuma registrada", "Sales: none recorded")
                : En ? $"Sales{(_mmS ? $" by sector ({VendasDeMm})" : "")} recorded through {NomeDoMes(um)} {ua}"
                : $"Vendas{(_mmS ? $" por setor ({VendasDeMm})" : "")} registradas até {NomeDoMes(um).ToLowerInvariant()}/{ua}",
            Conv.AtualizadoEm is { } q
                ? (En ? $"proposals read from the spreadsheets on {q.ToString("MM/dd/yyyy 'at' HH:mm", EnUs)}" : $"propostas lidas das planilhas em {q:dd/MM/yyyy 'às' HH:mm}")
                : T("propostas: planilhas ainda não lidas", "proposals: spreadsheets not read yet"),
            En ? $"{NomeDoMes(hoje.Month)} {hoje.Year} in progress (partial proposals)" : $"{NomeDoMes(hoje.Month).ToLowerInvariant()}/{hoje.Year} em andamento (propostas parciais)",
        };
        return string.Join(" · ", partes);
    }
}
