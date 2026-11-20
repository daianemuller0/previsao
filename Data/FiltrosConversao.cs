namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Filtros da guia Conversão (Ano, Mês, Setor, Vendedor, BU, BU Intercompany,
// Mercado, País, Produto) num objeto só, para serem compartilhados entre a
// barra de filtros e as seções embutidas na guia Performance Comercial — e
// aplicados também às propostas da própria Performance Comercial. Mesmas
// regras do Aplicar() da guia Conversão.
// ---------------------------------------------------------------------------
public sealed class FiltrosConversao
{
    private static HashSet<string> Novo() => new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Anos { get; } = Novo();
    public HashSet<string> Meses { get; } = Novo();
    public HashSet<string> Vendedores { get; } = Novo();
    public HashSet<string> Bus { get; } = Novo();
    public HashSet<string> BuIcs { get; } = Novo();
    public HashSet<string> Origens { get; } = Novo();
    public HashSet<string> Mercados { get; } = Novo();
    public HashSet<string> Paises { get; } = Novo();
    public HashSet<string> Produtos { get; } = Novo();

    /// <summary>
    /// Base da taxa de conversão: true = todas as propostas enviadas (Ganhas ÷ tudo, abertas e não
    /// classificadas inclusive — o padrão); false = só as decididas (Ganhas ÷ (Ganhas + Perdidas)).
    /// </summary>
    public bool BaseTodas { get; set; } = true;

    /// <summary>
    /// Como a taxa de conversão é calculada: false = por quantidade de propostas (o padrão);
    /// true = por valor (R$ ganho ÷ R$ da base escolhida em "Taxa sobre").
    /// </summary>
    public bool PorValor { get; set; }

    /// <summary>
    /// Primeiro ano considerado (null = todos). Na guia Performance Comercial é 2022: propostas
    /// mais antigas, do futuro ou sem data de envio ficam fora da barra de filtros e das seções.
    /// </summary>
    public int? AnoMinimo { get; init; }

    /// <summary>A data está dentro dos anos considerados (sempre true sem ano mínimo).</summary>
    public bool NoPeriodo(DateTime? data) =>
        AnoMinimo is not int min || data is DateTime d && d.Year >= min && d.Year <= DateTime.Today.Year;

    private IEnumerable<HashSet<string>> Todos => new[] { Anos, Meses, Vendedores, Bus, BuIcs, Origens, Mercados, Paises, Produtos };

    /// <summary>Quantos filtros estão ligados.</summary>
    public int Ativos => Todos.Count(s => s.Count > 0);

    /// <summary>Retrato dos filtros: muda sempre que algum muda.</summary>
    public string Assinatura() =>
        (BaseTodas ? "T|" : "D|") + (PorValor ? "V|" : "Q|") + string.Join("|", Todos.Select(s => string.Join(",", s.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))));

    /// <summary>
    /// A proposta passa pelos filtros? (as mesmas regras da guia Conversão)
    /// ignorarAno = true: aplica todos menos o de Ano (para comparar com o ano anterior, que pode não estar escolhido).
    /// </summary>
    public bool Passa(PropostaConv p) => Passa(p, false);

    public bool Passa(PropostaConv p, bool ignorarAno)
    {
        if (!NoPeriodo(p.Data)) return false;
        if (!ignorarAno && Anos.Count > 0 && !Anos.Contains(p.Data?.Year.ToString() ?? "Sem data")) return false;
        if (Meses.Count > 0 && !(p.Data is DateTime d && Meses.Contains(Fmt.MonthLong(d.Month)))) return false;
        if (Vendedores.Count > 0 && !Vendedores.Contains(p.Vendedor)) return false;
        if (Bus.Count > 0 && !Bus.Contains(p.Bu)) return false;
        if (BuIcs.Count > 0 && !BuIcs.Contains(p.BuIc)) return false;
        if (Origens.Count > 0 && !Origens.Contains(p.Origem)) return false;
        if (Mercados.Count > 0 && !Mercados.Contains(p.Mercado)) return false;
        if (Paises.Count > 0 && !Paises.Contains(p.Pais)) return false;
        if (Produtos.Count > 0 && !Produtos.Contains(p.Produto)) return false;
        return true;
    }
}
