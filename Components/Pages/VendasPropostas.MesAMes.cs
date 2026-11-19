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
    private string _mmModo = "comparar";
    private int? _mmComp;
    private bool _mmDetalhe;
    /// <summary>Popover "Cores dos anos" aberto (mini-gráficos).</summary>
    private bool _mmCores;

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
        if (_meses.FirstOrDefault(x => x.Ano == a && x.Mes == m)?.Vendas is decimal v) return new(SitMes.Valor, v, false);
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
        var hoje = DateTime.Today;
        return a < hoje.Year ? 12 : a > hoje.Year ? 0 : hoje.Month - 1;
    }

    /// <summary>Soma dos meses 1..ate que têm valor, e quantos meses entraram.</summary>
    private (decimal Soma, int Meses) SomaMm(bool vendas, int a, int ate)
    {
        decimal s = 0; var n = 0;
        for (var m = 1; m <= ate; m++)
        {
            var c = Cel(vendas, a, m);
            if (!c.Tem || c.Parcial) continue;
            s += c.Valor; n++;
        }
        return (s, n);
    }

    /// <summary>O ano comparado: o escolhido; senão o anterior ao principal; senão o mais próximo.</summary>
    private int? CompMm(int principal)
    {
        var anos = AnosDoPainel.Where(a => a != principal).ToList();
        if (anos.Count == 0) return null;
        if (_mmComp is int c && anos.Contains(c)) return c;
        if (anos.Contains(principal - 1)) return principal - 1;
        return anos.OrderBy(a => Math.Abs(a - principal)).ThenByDescending(a => a).First();
    }

    private sealed record LadoMm(int Ano, decimal Vendas, int MesesV, int Propostas, int MesesP, double? Taxa, int G, int Base);

    /// <summary>
    /// Resumo de dois anos no período comparável: vendas de jan até o último mês fechado nos dois;
    /// propostas e conversão de jan até o último mês fechado nos dois (o mês em andamento fica fora).
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
            return new LadoMm(a, v, nv, (int)p, np, mp > 0 ? t.Taxa : null, t.G, t.Base);
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

    /// <summary>R$ em milhões com uma casa, sem a unidade (o título do gráfico diz "R$ milhões"): "31,4", "−6,1".</summary>
    private static string MiCurto(decimal v) => (v < 0 ? "−" : "") + (Math.Abs(v) / 1_000_000m).ToString("#,##0.0", Br);

    /// <summary>Valor exato, com centavos: "R$ 31.412.345,67".</summary>
    private static string Reais(decimal v) => (v < 0 ? "−" : "") + "R$ " + Math.Abs(v).ToString("#,##0.00", Br);

    private static string MesesTxt(int ate) => ate <= 0 ? "sem meses fechados" : ate >= 12 ? "jan–dez" : ate == 1 ? "jan" : $"jan–{MesCurto(ate).ToLowerInvariant()}";

    /// <summary>Variação % (null quando a base é zero ou negativa: a conta não tem sentido).</summary>
    private static double? VarMm(decimal atual, decimal baseAnt) => baseAnt > 0 ? (double)((atual - baseAnt) / baseAnt * 100) : null;

    private static string PctSinal(double v) => (v > 0.05 ? "+" : v < -0.05 ? "−" : "") + Math.Abs(v).ToString("0.0", Br) + "%";
    private static string PpSinal(double v) => (v > 0.05 ? "+" : v < -0.05 ? "−" : "") + Math.Abs(v).ToString("0.0", Br) + " p.p.";
    private static string DifReais(decimal d) => (d > 0 ? "+" : d < 0 ? "−" : "") + "R$ " + Compacto(Math.Abs(d));
    private static string DifQtd(int d) => (d > 0 ? "+" : d < 0 ? "−" : "") + Math.Abs(d).ToString("#,##0", Br);

    /// <summary>Classe de cor da variação: vendas e conversão sobem = bom; propostas são neutras (mais propostas não é, por si, melhor).</summary>
    private static string ClsVar(double? v, bool neutra) => v is not double x || Math.Abs(x) < 0.05 ? "neu" : neutra ? "neu" : x > 0 ? "pos" : "neg";
    private static string Seta(double? v) => v is not double x || Math.Abs(x) < 0.05 ? "" : x > 0 ? "▲ " : "▼ ";

    private string CelTxt(bool vendas, CelMm c, bool exato) => c.Sit switch
    {
        SitMes.SemDado => "n/d",
        SitMes.AApurar => "a apurar",
        _ => vendas ? (exato ? Reais(c.Valor) : MiCurto(c.Valor)) : N0((int)c.Valor) + (c.Parcial && exato ? " (parcial)" : ""),
    };

    /// <summary>Linha de situação dos dados, no topo da seção.</summary>
    private string StatusMm()
    {
        var hoje = DateTime.Today;
        var ultV = _meses.Where(m => m.Vendas is not null).OrderBy(m => m.Ano * 100 + m.Mes).LastOrDefault();
        var partes = new List<string>
        {
            ultV is null ? "Vendas: nenhuma registrada" : $"Vendas registradas até {NomeDoMes(ultV.Mes).ToLowerInvariant()}/{ultV.Ano}",
            Conv.AtualizadoEm is { } q ? $"propostas lidas das planilhas em {q:dd/MM/yyyy 'às' HH:mm}" : "propostas: planilhas ainda não lidas",
            $"{NomeDoMes(hoje.Month).ToLowerInvariant()}/{hoje.Year} em andamento (propostas parciais)",
        };
        return string.Join(" · ", partes);
    }
}
