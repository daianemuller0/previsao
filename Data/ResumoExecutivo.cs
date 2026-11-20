using ClosedXML.Excel;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// RESUMO EXECUTIVO | GLOBAL COMMERCIAL PERFORMANCE — os cálculos (servidor).
//
// Fontes (as mesmas dos painéis atuais, sem regra nova):
//  · Propostas: planilhas da guia Conversão (AFM e NB), pela DATA DE ENVIO.
//    - Enviadas e valor enviado: cada número de proposta conta uma vez, como na
//      Performance Comercial (ConversaoDeVendas.PropostasEnviadas: o envio mais
//      antigo, o maior valor); meses informados à mão valem quando não há filtro.
//    - Ganhas, perdidas, abertas e taxas: as linhas das planilhas, como na guia
//      Conversão (decididas = ganhas + perdidas; por valor = R$ ganho ÷ R$ da base).
//  · Vendas: histórico da equipe, empresa toda, por mês de faturamento.
//  · Metas: budgets anuais HSA e Grupo (não há metas mensais, por BU nem por setor).
// A base não tem data de fechamento das propostas nem vendas por BU, país, setor,
// produto ou vendedor: nessas visões usa-se o VALOR GANHO, sempre com esse nome.
// Nada aqui grava dados, nem expõe cliente ou número de proposta.
// ---------------------------------------------------------------------------

/// <summary>Os filtros da página Resumo Executivo.</summary>
public sealed class FiltroResumo
{
    private static HashSet<string> Novo() => new(StringComparer.OrdinalIgnoreCase);
    public int AnoDe { get; set; } = 2022;
    public int AnoAte { get; set; } = DateTime.Today.Year;
    public HashSet<string> Setores { get; } = Novo();
    public HashSet<string> Bus { get; } = Novo();
    public HashSet<string> Paises { get; } = Novo();
    public HashSet<string> Segmentos { get; } = Novo();
    public HashSet<string> Produtos { get; } = Novo();
    public HashSet<string> Vendedores { get; } = Novo();
    /// <summary>Base das taxas: false = decididas (padrão); true = todas as propostas enviadas.</summary>
    public bool BaseTodas { get; set; }

    /// <summary>Algum filtro de dimensão ligado (as vendas, que são da empresa toda, não seguem esses filtros).</summary>
    public bool TemDimensao => new[] { Setores, Bus, Paises, Segmentos, Produtos, Vendedores }.Any(s => s.Count > 0);

    public bool Passa(PropostaConv p) =>
        (Setores.Count == 0 || Setores.Contains(p.Origem)) &&
        (Bus.Count == 0 || Bus.Contains(Rot(p.Bu))) &&
        (Paises.Count == 0 || Paises.Contains(Rot(p.Pais))) &&
        (Segmentos.Count == 0 || Segmentos.Contains(Rot(p.Mercado))) &&
        (Produtos.Count == 0 || Produtos.Contains(Rot(p.Produto))) &&
        (Vendedores.Count == 0 || Vendedores.Contains(Rot(p.Vendedor)));

    public static string Rot(string? s) => string.IsNullOrWhiteSpace(s) ? ConversaoService.NaoInformado : s.Trim();
}

/// <summary>Uma linha das planilhas, já com o valor em R$ e a marca de "representa o número da proposta".</summary>
public sealed record RegistroResumo(PropostaConv P, int Ano, int Mes, double? Brl, bool Representa, decimal ValorEnviado, bool SemTaxa, bool Cancelada);

/// <summary>Indicadores somáveis de um recorte (mês, ano, BU…). As taxas saem das somas.</summary>
public sealed class Indicadores
{
    public int Enviadas, ComValor, SemTaxa;
    public decimal ValorEnviado;
    public int Linhas, G, P, A, NC, Canceladas;
    public double VG, VP, VA, VNC;
    public bool BaseTodas;

    public int Decididas => G + P;
    public double VTotal => VG + VP + VA + VNC;
    public double? TaxaQtdDecididas => G + P > 0 ? G * 100.0 / (G + P) : null;
    public double? TaxaQtdTodas => Linhas > 0 ? G * 100.0 / Linhas : null;
    public double? TaxaValorDecididas => VG + VP > 0 ? VG / (VG + VP) * 100 : null;
    public double? TaxaValorTodas => VTotal > 0 ? VG / VTotal * 100 : null;
    /// <summary>Conversão por quantidade, pela base escolhida ("Taxa sobre").</summary>
    public double? TaxaQtd => BaseTodas ? TaxaQtdTodas : TaxaQtdDecididas;
    /// <summary>Conversão por valor, pela base escolhida.</summary>
    public double? TaxaValor => BaseTodas ? TaxaValorTodas : TaxaValorDecididas;
    public decimal? ValorMedio => ComValor > 0 ? ValorEnviado / ComValor : null;
    /// <summary>Valor médio das propostas ganhas (a base não traz a quantidade de vendas).</summary>
    public decimal? TicketGanhas => G > 0 && VG > 0 ? (decimal)VG / G : null;
    public bool Vazio => Linhas == 0 && Enviadas == 0;

    public void Somar(Indicadores o)
    {
        Enviadas += o.Enviadas; ComValor += o.ComValor; SemTaxa += o.SemTaxa; ValorEnviado += o.ValorEnviado;
        Linhas += o.Linhas; G += o.G; P += o.P; A += o.A; NC += o.NC; Canceladas += o.Canceladas;
        VG += o.VG; VP += o.VP; VA += o.VA; VNC += o.VNC;
    }

    public void Somar(RegistroResumo r)
    {
        Linhas++;
        var v = r.Brl ?? 0;
        switch (r.P.Classe)
        {
            case ClasseProposta.Ganha: G++; VG += v; break;
            case ClasseProposta.Perdida: P++; VP += v; break;
            case ClasseProposta.Aberta: A++; VA += v; break;
            default: NC++; VNC += v; break;
        }
        if (r.Cancelada) Canceladas++;
    }

    public void SomarEnvio(RegistroResumo r)
    {
        Enviadas++; ValorEnviado += r.ValorEnviado;
        if (r.ValorEnviado > 0) ComValor++;
        if (r.SemTaxa) SemTaxa++;
    }
}

/// <summary>Um fato encontrado nos números: o resultado, a evidência e o que investigar (sem explicar a causa).</summary>
public sealed record InsightResumo(string Tema, string Resultado, string Evidencia, string Atencao, bool Conclusivo = true);

/// <summary>Uma dimensão de análise (BU, país, setor…) com os indicadores por chave, ano e mês.</summary>
public sealed class DimensaoResumo
{
    public string Id = "", Nome = "";
    public Dictionary<(string Chave, int Ano, int Mes), Indicadores> Celulas = new();
    public List<string> Chaves = new();
}

/// <summary>Tudo o que a página e a exportação mostram, calculado uma vez para os filtros atuais.</summary>
public sealed class ModeloResumo
{
    public List<int> Anos = new();
    public int Principal, Comparativo;
    /// <summary>Ano em curso dentro do período (0 = nenhum) e o último mês fechado dele.</summary>
    public int AnoIncompleto, MesCorte;
    /// <summary>true = todos os anos de janeiro até o MesCorte (período equivalente).</summary>
    public bool Equivalente;
    public bool BaseTodas, FiltroDimensao;
    public Dictionary<(int Ano, int Mes), Indicadores> Mes = new();
    public Dictionary<(int Ano, int Mes), decimal> VendasMes = new();
    public Dictionary<int, (decimal? Hsa, decimal? Grupo)> Metas = new();
    public Dictionary<string, DimensaoResumo> Dim = new();
    public List<InsightResumo> Insights = new();

    /// <summary>Até que mês cada ano entra na tela: o corte (período equivalente) ou dezembro.</summary>
    public int Ate => Equivalente && AnoIncompleto > 0 && MesCorte > 0 ? MesCorte : 12;
    /// <summary>Até que mês comparar dois anos: se um deles está em curso, só os meses fechados dele.</summary>
    public int AteComparavel(int a, int b) => (a == AnoIncompleto || b == AnoIncompleto) && MesCorte > 0 ? MesCorte : Ate;
    public bool Parcial(int ano) => ano == AnoIncompleto && !(Equivalente && MesCorte > 0);
    public string PeriodoTxt(int ate) => ate >= 12 ? "jan–dez" : ate == 1 ? "jan" : "jan–" + MesCurto(ate);

    public Indicadores Ano(int ano, int? ate = null) => Soma(Mes.Where(kv => kv.Key.Ano == ano && kv.Key.Mes <= (ate ?? Ate)).Select(kv => kv.Value));
    public decimal? Vendas(int ano, int? ate = null)
    {
        var l = VendasMes.Where(kv => kv.Key.Ano == ano && kv.Key.Mes <= (ate ?? Ate)).ToList();
        return l.Count == 0 ? null : l.Sum(kv => kv.Value);
    }
    public int UltimoMesVendas(int ano) => VendasMes.Keys.Where(k => k.Ano == ano).Select(k => k.Mes).DefaultIfEmpty(0).Max();
    public Indicadores DimAno(string dim, string chave, int ano, int? ate = null) =>
        Soma(Dim[dim].Celulas.Where(kv => kv.Key.Chave == chave && kv.Key.Ano == ano && kv.Key.Mes <= (ate ?? Ate)).Select(kv => kv.Value));

    public Indicadores Soma(IEnumerable<Indicadores> l)
    {
        var r = new Indicadores { BaseTodas = BaseTodas };
        foreach (var i in l) r.Somar(i);
        return r;
    }

    public static readonly string[] Meses = { "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez" };
    public static string MesCurto(int m) => Meses[m - 1];
}

public static class ResumoExecutivoCalc
{
    public static readonly (string Id, string Nome)[] Dimensoes =
    {
        ("setor", "Setor (AFM × NB)"), ("bu", "Business Unit"), ("pais", "País"), ("segmento", "Segmento (mercado)"), ("produto", "Produto"), ("vendedor", "Vendedor"),
    };

    public static string ChaveDim(string dim, PropostaConv p) => dim switch
    {
        "setor" => p.Origem,
        "bu" => FiltroResumo.Rot(p.Bu),
        "pais" => FiltroResumo.Rot(p.Pais),
        "segmento" => FiltroResumo.Rot(p.Mercado),
        "produto" => FiltroResumo.Rot(p.Produto),
        _ => FiltroResumo.Rot(p.Vendedor),
    };

    /// <summary>
    /// As linhas do recorte, com o valor em R$ e o "representante" de cada número de proposta (mesma regra da
    /// Performance Comercial: deduplica no recorte inteiro, fica o envio mais antigo e o maior valor).
    /// </summary>
    public static List<RegistroResumo> Registros(IEnumerable<PropostaConv> propostas, IReadOnlyDictionary<int, double> taxas, int anoDe, int anoAte)
    {
        var l = propostas.Where(p => p.Data is not null).Select(p =>
        {
            var d = p.Data!.Value;
            double? brl = p.ValorBrl ?? (p.ValorUsd is double u && taxas.TryGetValue(d.Year, out var t) ? u * t : null);
            var semTaxa = p.ValorBrl is null && p.ValorUsd is not null && brl is null;
            var cod = p.Chave.Trim().Length > 0 ? p.Origem + ":" + p.Chave.Trim().ToUpperInvariant() : $"{p.Origem}#{p.Linha}";
            return (P: p, D: d, Brl: brl, SemTaxa: semTaxa, Cod: cod);
        }).ToList();
        var rep = l.GroupBy(x => x.Cod).ToDictionary(g => g.Key, g =>
        {
            var primeiro = g.OrderBy(x => x.D.Date).First();
            return (Rep: primeiro.P, Valor: g.Max(x => (decimal)Math.Round(x.Brl ?? 0, 2)), primeiro.SemTaxa);
        });
        return l.Where(x => x.D.Year >= anoDe && x.D.Year <= anoAte).Select(x =>
        {
            var r = rep[x.Cod];
            var representa = ReferenceEquals(r.Rep, x.P);
            return new RegistroResumo(x.P, x.D.Year, x.D.Month, x.Brl, representa, representa ? r.Valor : 0m, representa && r.SemTaxa,
                x.P.StatusOriginal.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    public static ModeloResumo Montar(IEnumerable<PropostaConv> todas, IReadOnlyDictionary<int, double> taxas,
        IReadOnlyList<MesDeVendas> vendas, IReadOnlyList<BudgetDoAno> budgets, FiltroResumo f, int principal, int comparativo, bool equivalente)
    {
        var hoje = DateTime.Today;
        var m = new ModeloResumo
        {
            Anos = Enumerable.Range(f.AnoDe, Math.Max(0, f.AnoAte - f.AnoDe + 1)).ToList(),
            BaseTodas = f.BaseTodas, FiltroDimensao = f.TemDimensao, Equivalente = equivalente,
        };
        m.Principal = m.Anos.Contains(principal) ? principal : m.Anos.DefaultIfEmpty(hoje.Year).Max();
        m.Comparativo = m.Anos.Contains(comparativo) && comparativo != m.Principal ? comparativo : m.Anos.Where(a => a < m.Principal).DefaultIfEmpty(m.Principal).Max();

        foreach (var v in vendas.Where(v => v.Vendas is not null && v.Ano >= f.AnoDe && v.Ano <= f.AnoAte)) m.VendasMes[(v.Ano, v.Mes)] = v.Vendas!.Value;
        foreach (var b in budgets.Where(b => b.Ano >= f.AnoDe && b.Ano <= f.AnoAte)) m.Metas[b.Ano] = (b.Hsa, b.Grupo);

        if (m.Anos.Contains(hoje.Year))
        {
            m.AnoIncompleto = hoje.Year;
            var mv = m.UltimoMesVendas(hoje.Year);
            var mp = hoje.Month - 1;                         // o mês corrente das propostas ainda está em andamento
            m.MesCorte = mv > 0 ? Math.Min(mv, mp) : mp;
        }

        var regs = Registros(todas.Where(f.Passa), taxas, f.AnoDe, f.AnoAte);
        Indicadores Nova() => new() { BaseTodas = f.BaseTodas };

        foreach (var r in regs)
        {
            if (!m.Mes.TryGetValue((r.Ano, r.Mes), out var i)) m.Mes[(r.Ano, r.Mes)] = i = Nova();
            i.Somar(r);
            if (r.Representa) i.SomarEnvio(r);
        }
        // meses de propostas informados à mão (histórico de antes do sistema): valem quando não há filtro, como na Performance
        if (!f.TemDimensao)
            foreach (var v in vendas.Where(v => v.Propostas is not null && v.Ano >= f.AnoDe && v.Ano <= f.AnoAte))
            {
                if (!m.Mes.TryGetValue((v.Ano, v.Mes), out var i)) m.Mes[(v.Ano, v.Mes)] = i = Nova();
                i.Enviadas = v.Propostas!.Value; i.ValorEnviado = v.ValorProposto ?? 0m;
                i.ComValor = v.ValorProposto is > 0 ? v.Propostas.Value : 0; i.SemTaxa = 0;
            }

        foreach (var (id, nome) in Dimensoes)
        {
            var d = new DimensaoResumo { Id = id, Nome = nome };
            foreach (var r in regs)
            {
                var k = (ChaveDim(id, r.P), r.Ano, r.Mes);
                if (!d.Celulas.TryGetValue(k, out var i)) d.Celulas[k] = i = Nova();
                i.Somar(r);
                if (r.Representa) i.SomarEnvio(r);
            }
            m.Dim[id] = d;
            // ordem: maior valor ganho no ano principal, depois maior valor enviado
            d.Chaves = d.Celulas.Keys.Select(k => k.Chave).Distinct()
                .OrderByDescending(c => m.DimAno(id, c, m.Principal).VG).ThenByDescending(c => m.DimAno(id, c, m.Principal).ValorEnviado).ThenBy(c => c).ToList();
        }
        m.Insights = Insights(m);
        return m;
    }

    // ================= Executive Insights =================

    private static string Mi(decimal v) => (v < 0 ? "−" : "") + "R$ " + (Math.Abs(v) >= 1_000_000_000m ? (Math.Abs(v) / 1_000_000_000m).ToString("#,##0.00", Br) + " bi" : (Math.Abs(v) / 1_000_000m).ToString("#,##0.0", Br) + " mi");
    private static string Mi(double v) => Mi((decimal)v);
    private static string Pc(double v) => v.ToString("0.0", Br) + "%";
    private static string Var(decimal a, decimal b) => b > 0 ? ((a - b) / b * 100).ToString("+0.0;−0.0;0.0", Br) + "%" : "—";
    private static readonly System.Globalization.CultureInfo Br = new("pt-BR");

    public static List<InsightResumo> Insights(ModeloResumo m)
    {
        var l = new List<InsightResumo>();
        var p = m.Principal; var c = m.Comparativo;
        var ate = m.AteComparavel(p, c); var per = m.PeriodoTxt(ate);

        // 1. vendas: ano principal × comparativo, no período equivalente
        if (m.Vendas(p, ate) is decimal vp && m.Vendas(c, ate) is decimal vc && vc > 0)
        {
            var cresceu = vp >= vc;
            l.Add(new("Vendas", $"Vendas {(cresceu ? "cresceram" : "caíram")} {Var(vp, vc)} em {p} contra {c} ({per}).",
                $"{Mi(vp)} em {p} × {Mi(vc)} em {c}, mesmos meses ({per}); diferença de {Mi(vp - vc)}.",
                "Avaliar os fatores associados à variação (mix de produtos, BUs, sazonalidade); a base não indica a causa."));
        }
        else l.Add(new("Vendas", $"Não há vendas registradas nos dois anos ({p} e {c}) para comparar.", "—", "Completar o histórico de vendas para concluir.", false));

        // 2. maior e menor ano de vendas (no período mostrado)
        var anosV = m.Anos.Select(a => (a, v: m.Vendas(a))).Where(x => x.v is not null).ToList();
        if (anosV.Count >= 2)
        {
            var mx = anosV.MaxBy(x => x.v!.Value); var mn = anosV.MinBy(x => x.v!.Value);
            l.Add(new("Vendas por ano", $"Maior volume de vendas: {mx.a}; menor: {mn.a} ({m.PeriodoTxt(m.Ate)}{(m.Ate < 12 ? ", todos os anos nos mesmos meses" : "")}).",
                $"{mx.a}: {Mi(mx.v!.Value)} · {mn.a}: {Mi(mn.v!.Value)}." + (m.Ate >= 12 && m.AnoIncompleto > 0 ? $" {m.AnoIncompleto} ainda está em curso (ano incompleto)." : ""),
                m.Ate >= 12 && m.AnoIncompleto > 0 ? "Para comparar com o ano em curso, use “Período equivalente”." : "—"));
        }

        // 3. melhor e pior mês de vendas
        var meses = m.VendasMes.Where(kv => kv.Key.Mes <= m.Ate).ToList();
        if (meses.Count >= 2)
        {
            var mx = meses.MaxBy(kv => kv.Value); var mn = meses.MinBy(kv => kv.Value);
            l.Add(new("Mês de vendas", $"Melhor mês: {ModeloResumo.MesCurto(mx.Key.Mes)}/{mx.Key.Ano}; pior: {ModeloResumo.MesCurto(mn.Key.Mes)}/{mn.Key.Ano} (meses de {m.PeriodoTxt(m.Ate)}, {m.Anos.First()}–{m.Anos.Last()}).",
                $"{Mi(mx.Value)} × {Mi(mn.Value)}{(mn.Value < 0 ? " (venda negativa no mês)" : "")}.",
                mn.Value < 0 ? "Verificar o lançamento negativo (estorno/ajuste) no histórico de vendas." : "Sazonalidade e eventos pontuais exigem análise caso a caso."));
        }

        // 4. conversão: principal × comparativo, mesmos meses
        var ip = m.Ano(p, ate); var ic = m.Ano(c, ate);
        if (ip.TaxaQtd is double tp && ic.TaxaQtd is double tc)
        {
            var dq = tp - tc;
            var tv = ip.TaxaValor is double vp2 && ic.TaxaValor is double vc2 ? $" Por valor: {Pc(vp2)} × {Pc(vc2)} ({(vp2 - vc2).ToString("+0.0;−0.0;0.0", Br)} p.p.)." : "";
            l.Add(new("Conversão", $"A taxa de conversão por quantidade {(dq >= 0 ? "subiu" : "caiu")} {Math.Abs(dq).ToString("0.0", Br)} p.p. ({c} → {p}, {per}).",
                $"{Pc(tp)} em {p} ({ip.G} ganhas de {(m.BaseTodas ? ip.Linhas : ip.Decididas)} {(m.BaseTodas ? "enviadas" : "decididas")}) × {Pc(tc)} em {c}.{tv}",
                "As propostas mais recentes ainda podem estar abertas: a taxa do ano em curso tende a mudar conforme forem decididas."));
        }

        // 5. propostas: volume de envio
        if (ip.Enviadas > 0 && ic.Enviadas > 0)
            l.Add(new("Propostas", $"Propostas enviadas: {ip.Enviadas.ToString("#,##0", Br)} em {p} × {ic.Enviadas.ToString("#,##0", Br)} em {c} ({Var(ip.Enviadas, ic.Enviadas)}, {per}).",
                $"Valor enviado: {Mi(ip.ValorEnviado)} × {Mi(ic.ValorEnviado)} ({Var(ip.ValorEnviado, ic.ValorEnviado)}).",
                "Mais ou menos propostas não é, por si, melhora ou piora; ler junto com a conversão e o valor ganho."));

        // 6. contribuição por BU / setor / segmento / produto (valor ganho no ano principal)
        void Topo(string dim, string tema, string rotulo)
        {
            if (!m.Dim.TryGetValue(dim, out var d)) return;
            var lst = d.Chaves.Select(k => (k, i: m.DimAno(dim, k, p))).Where(x => x.i.VG > 0).ToList();
            var tot = lst.Sum(x => x.i.VG);
            if (lst.Count == 0 || tot <= 0) { l.Add(new(tema, $"Sem valor ganho por {rotulo} em {p} no recorte.", "—", "—", false)); return; }
            var t = lst.MaxBy(x => x.i.VG);
            l.Add(new(tema, $"{t.k} tem a maior contribuição em valor ganho em {p} ({rotulo}).",
                $"{Mi(t.i.VG)} de {Mi(tot)} ({Pc(t.i.VG / tot * 100)} do valor ganho) · conversão {(t.i.TaxaQtd is double tx ? Pc(tx) : "—")}.",
                "Valor ganho são as propostas ganhas; não é a venda faturada (a base não tem vendas por " + rotulo + ")."));
        }
        Topo("bu", "Business Units", "BU");
        Topo("segmento", "Segmentos", "segmento");
        Topo("produto", "Produtos", "produto");

        // 7. AFM × NB: volume × eficiência
        if (m.Dim.TryGetValue("setor", out var st))
        {
            var afm = m.DimAno("setor", ConversaoService.Afm, p); var nb = m.DimAno("setor", ConversaoService.Nb, p);
            if (afm.VG + nb.VG > 0 && afm.TaxaQtd is double ta && nb.TaxaQtd is double tn)
            {
                var vol = afm.VG >= nb.VG ? "AFM" : "NB"; var efi = ta >= tn ? "AFM" : "NB";
                l.Add(new("Aftermarket × NB", vol == efi ? $"{vol} lidera em volume (valor ganho) e em conversão em {p}." : $"{vol} lidera em volume (valor ganho), mas {efi} converte mais em {p}.",
                    $"Valor ganho: AFM {Mi(afm.VG)} × NB {Mi(nb.VG)} · conversão: AFM {Pc(ta)} × NB {Pc(tn)}.",
                    "Volume financeiro e eficiência comercial são medidas diferentes; ler as duas."));
            }
        }

        // 8. metas: desvio no ano principal e o maior/menor atingimento
        if (m.Metas.TryGetValue(p, out var mt) && mt.Hsa is decimal mh && m.Vendas(p, 12) is decimal vpa)
        {
            var parcial = p == m.AnoIncompleto;
            l.Add(new("Metas", $"{p}: {(vpa / mh * 100).ToString("0.0", Br)}% da Meta HSA{(parcial ? $" até {ModeloResumo.MesCurto(m.UltimoMesVendas(p))} (ano em curso)" : "")}.",
                $"Vendas {Mi(vpa)} × meta {Mi(mh)}; {(vpa >= mh ? "acima" : "faltam")} {Mi(Math.Abs(mh - vpa))}." + (mt.Grupo is decimal mg ? $" Meta Grupo: {(vpa / mg * 100).ToString("0.0", Br)}%." : ""),
                parcial ? "A meta é anual: o atingimento do ano em curso é parcial (não há meta mensal para comparar mês a mês)." : "—"));
        }
        var atg = m.Anos.Where(a => a != m.AnoIncompleto && m.Metas.TryGetValue(a, out var x) && x.Hsa is > 0 && m.Vendas(a, 12) is not null)
            .Select(a => (a, pct: m.Vendas(a, 12)!.Value / m.Metas[a].Hsa!.Value * 100)).ToList();
        if (atg.Count >= 2)
        {
            var mx = atg.MaxBy(x => x.pct); var mn = atg.MinBy(x => x.pct);
            l.Add(new("Metas (anos fechados)", $"Maior atingimento da Meta HSA: {mx.a} ({mx.pct.ToString("0.0", Br)}%); menor: {mn.a} ({mn.pct.ToString("0.0", Br)}%).",
                "Vendas do ano inteiro ÷ Meta HSA do ano.", "Metas definidas em contextos diferentes; comparar com cautela."));
        }
        return l;
    }

    // ================= Exportação para Excel (só dados agregados) =================

    public sealed record OpcoesExportacao(bool Mensal, bool Setor, bool Bu, bool Pais, bool Segmento, bool Produto, bool Vendedor, bool Metas, bool Funil, bool Insights);

    public static byte[] Excel(ModeloResumo m, OpcoesExportacao o, string filtrosTxt, string usuario, string fonteTxt)
    {
        using var wb = new XLWorkbook();
        const string FmtMi = "#,##0.00", FmtQ = "#,##0", FmtPct = "0.0\"%\"";
        var navy = XLColor.FromHtml("#172B43");

        IXLWorksheet Aba(string nome, string titulo)
        {
            var ws = wb.Worksheets.Add(nome);
            ws.Cell(1, 1).Value = titulo; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14; ws.Cell(1, 1).Style.Font.FontColor = navy;
            ws.Cell(2, 1).Value = $"Filtros: {filtrosTxt} · Taxas sobre {(m.BaseTodas ? "todas as propostas" : "decididas")} · {(m.Ate < 12 ? $"período equivalente ({m.PeriodoTxt(m.Ate)})" : "ano inteiro")}";
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;
            return ws;
        }
        void Cab(IXLWorksheet ws, int lin, params string[] t)
        {
            for (var i = 0; i < t.Length; i++)
            {
                var c = ws.Cell(lin, i + 1); c.Value = t[i];
                c.Style.Font.Bold = true; c.Style.Font.FontColor = XLColor.White; c.Style.Fill.BackgroundColor = navy;
            }
        }
        void V(IXLCell c, double? v, string fmt) { if (v is double d) { c.Value = d; c.Style.NumberFormat.Format = fmt; } else c.Value = "—"; }
        void D(IXLCell c, decimal? v, string fmt) => V(c, v is decimal x ? (double)x : null, fmt);
        static double? Mi2(decimal? v) => v is decimal x ? (double)(x / 1_000_000m) : null;

        // 1. sumário e conclusões
        var s = Aba("Sumário", "Resumo Executivo | Global Commercial Performance");
        s.Cell(3, 1).Value = $"Gerado por {usuario} em {DateTime.Now:dd/MM/yyyy HH:mm} · somente dados agregados (sem clientes nem números de proposta) · confidencial";
        s.Cell(4, 1).Value = fonteTxt;
        var l = 6;
        if (o.Insights)
        {
            Cab(s, l++, "Tema", "Resultado", "Evidência", "Ponto de atenção");
            foreach (var i in m.Insights) { s.Cell(l, 1).Value = i.Tema; s.Cell(l, 2).Value = i.Resultado; s.Cell(l, 3).Value = i.Evidencia; s.Cell(l, 4).Value = i.Atencao; l++; }
        }
        s.Column(1).Width = 22; s.Columns(2, 4).Width = 70;

        // 2. anual
        var a = Aba("Anual", "Resultados anuais (R$ em milhões)");
        Cab(a, 4, new[] { "Indicador" }.Concat(m.Anos.Select(x => x + (m.Parcial(x) ? " (parcial)" : ""))).ToArray());
        var linhas = new (string Nome, Func<int, double?> F, string Fmt)[]
        {
            ("Vendas realizadas (R$ mi, empresa toda)", x => Mi2(m.Vendas(x)), FmtMi),
            ("Propostas enviadas", x => m.Ano(x).Enviadas, FmtQ),
            ("Valor enviado (R$ mi)", x => Mi2(m.Ano(x).ValorEnviado), FmtMi),
            ("Valor médio por proposta (R$ mi)", x => Mi2(m.Ano(x).ValorMedio), FmtMi),
            ("Venda por proposta enviada (R$ mi)", x => m.Vendas(x) is decimal vv && m.Ano(x).Enviadas > 0 ? Mi2(vv / m.Ano(x).Enviadas) : null, "#,##0.000"),
            ("Ganhas", x => m.Ano(x).G, FmtQ), ("Perdidas", x => m.Ano(x).P, FmtQ), ("  das quais canceladas", x => m.Ano(x).Canceladas, FmtQ),
            ("Abertas", x => m.Ano(x).A, FmtQ), ("Não classificadas", x => m.Ano(x).NC, FmtQ),
            ("Conversão por quantidade (%)", x => m.Ano(x).TaxaQtd, FmtPct), ("Conversão por valor (%)", x => m.Ano(x).TaxaValor, FmtPct),
            ("Valor ganho (R$ mi)", x => m.Ano(x).VG / 1e6, FmtMi),
            ("Meta HSA (R$ mi, anual)", x => Mi2(m.Metas.TryGetValue(x, out var mt) ? mt.Hsa : null), FmtMi),
            ("Atingimento Meta HSA (%)", x => m.Metas.TryGetValue(x, out var mt) && mt.Hsa is > 0 && m.Vendas(x) is decimal vv ? (double)(vv / mt.Hsa.Value * 100) : null, FmtPct),
            ("Meta Grupo (R$ mi, anual)", x => Mi2(m.Metas.TryGetValue(x, out var mt) ? mt.Grupo : null), FmtMi),
            ("Atingimento Meta Grupo (%)", x => m.Metas.TryGetValue(x, out var mt) && mt.Grupo is > 0 && m.Vendas(x) is decimal vv ? (double)(vv / mt.Grupo.Value * 100) : null, FmtPct),
            ("Crescimento das vendas vs ano anterior (%)", x => m.Anos.Contains(x - 1) && m.Vendas(x, m.AteComparavel(x, x - 1)) is decimal v1 && m.Vendas(x - 1, m.AteComparavel(x, x - 1)) is decimal v0 && v0 > 0 ? (double)((v1 - v0) / v0 * 100) : null, FmtPct),
        };
        l = 5;
        foreach (var (nome, fn, fmt) in linhas)
        {
            a.Cell(l, 1).Value = nome;
            for (var i = 0; i < m.Anos.Count; i++) V(a.Cell(l, i + 2), fn(m.Anos[i]), fmt);
            l++;
        }
        a.Column(1).Width = 44; a.Columns(2, m.Anos.Count + 1).Width = 16;

        // 3. mensal (formato longo: ano × mês)
        if (o.Mensal)
        {
            var w = Aba("Mensal", "Mês a mês: vendas por mês de faturamento · propostas por mês de envio (coorte: status de hoje)");
            Cab(w, 4, "Ano", "Mês", "Vendas no mês (R$ mi)", "Valor enviado (R$ mi)", "Propostas enviadas", "Ganhas", "Perdidas", "Abertas", "Conversão qtd (%)", "Conversão valor (%)", "Valor médio proposta (R$ mi)", "Valor médio das ganhas (R$ mi)");
            l = 5;
            foreach (var ano in m.Anos)
                for (var mes = 1; mes <= 12; mes++)
                {
                    m.Mes.TryGetValue((ano, mes), out var i); i ??= new Indicadores { BaseTodas = m.BaseTodas };
                    var temV = m.VendasMes.TryGetValue((ano, mes), out var vm);
                    if (i.Vazio && !temV) continue;
                    w.Cell(l, 1).Value = ano; w.Cell(l, 2).Value = ModeloResumo.MesCurto(mes);
                    V(w.Cell(l, 3), temV ? (double)vm / 1e6 : null, FmtMi); V(w.Cell(l, 4), (double)i.ValorEnviado / 1e6, FmtMi);
                    V(w.Cell(l, 5), i.Enviadas, FmtQ); V(w.Cell(l, 6), i.G, FmtQ); V(w.Cell(l, 7), i.P, FmtQ); V(w.Cell(l, 8), i.A, FmtQ);
                    V(w.Cell(l, 9), i.TaxaQtd, FmtPct); V(w.Cell(l, 10), i.TaxaValor, FmtPct); D(w.Cell(l, 11), i.ValorMedio / 1_000_000m, FmtMi); D(w.Cell(l, 12), i.TicketGanhas / 1_000_000m, FmtMi);
                    l++;
                }
            w.Columns(1, 12).Width = 16;
        }

        // 4. dimensões
        foreach (var (id, nome, ok) in new[] { ("setor", "AFM × NB", o.Setor), ("bu", "BU", o.Bu), ("pais", "País", o.Pais), ("segmento", "Segmento", o.Segmento), ("produto", "Produto", o.Produto), ("vendedor", "Vendedor", o.Vendedor) })
        {
            if (!ok || !m.Dim.TryGetValue(id, out var d)) continue;
            var w = Aba(nome, $"{d.Nome}: por ano (valor ganho = propostas ganhas; não é venda faturada)");
            Cab(w, 4, nome, "Ano", "Valor enviado (R$ mi)", "Propostas enviadas", "Ganhas", "Perdidas", "Abertas", "Conversão qtd (%)", "Conversão valor (%)", "Valor ganho (R$ mi)", "Participação no valor ganho (%)", "Valor médio proposta (R$ mi)");
            l = 5;
            foreach (var ano in m.Anos)
            {
                var tot = d.Chaves.Sum(k => m.DimAno(id, k, ano).VG);
                foreach (var k in d.Chaves)
                {
                    var i = m.DimAno(id, k, ano);
                    if (i.Vazio) continue;
                    w.Cell(l, 1).Value = k; w.Cell(l, 2).Value = ano;
                    V(w.Cell(l, 3), (double)i.ValorEnviado / 1e6, FmtMi); V(w.Cell(l, 4), i.Enviadas, FmtQ); V(w.Cell(l, 5), i.G, FmtQ); V(w.Cell(l, 6), i.P, FmtQ); V(w.Cell(l, 7), i.A, FmtQ);
                    V(w.Cell(l, 8), i.TaxaQtd, FmtPct); V(w.Cell(l, 9), i.TaxaValor, FmtPct); V(w.Cell(l, 10), i.VG / 1e6, FmtMi);
                    V(w.Cell(l, 11), tot > 0 ? i.VG / tot * 100 : null, FmtPct); D(w.Cell(l, 12), i.ValorMedio / 1_000_000m, FmtMi);
                    l++;
                }
            }
            w.Column(1).Width = 34; w.Columns(2, 12).Width = 16;
        }

        // 5. metas
        if (o.Metas)
        {
            var w = Aba("Metas", "Atingimento de metas (metas anuais; não há metas mensais, por BU nem por setor)");
            Cab(w, 4, "Ano", "Meta HSA (R$ mi)", "Meta Grupo (R$ mi)", "Vendas (R$ mi)", "Atingimento HSA (%)", "Atingimento Grupo (%)", "Diferença p/ Meta HSA (R$ mi)", "Vendas vs ano anterior (%)");
            l = 5;
            foreach (var ano in m.Anos)
            {
                m.Metas.TryGetValue(ano, out var mt); var vv = m.Vendas(ano, 12);
                w.Cell(l, 1).Value = ano + (ano == m.AnoIncompleto ? " (em curso)" : "");
                D(w.Cell(l, 2), mt.Hsa / 1_000_000m, FmtMi); D(w.Cell(l, 3), mt.Grupo / 1_000_000m, FmtMi); D(w.Cell(l, 4), vv / 1_000_000m, FmtMi);
                V(w.Cell(l, 5), mt.Hsa is > 0 && vv is decimal a1 ? (double)(a1 / mt.Hsa.Value * 100) : null, FmtPct);
                V(w.Cell(l, 6), mt.Grupo is > 0 && vv is decimal a2 ? (double)(a2 / mt.Grupo.Value * 100) : null, FmtPct);
                D(w.Cell(l, 7), mt.Hsa is decimal h && vv is decimal a3 ? (a3 - h) / 1_000_000m : null, FmtMi);
                var ac = m.AteComparavel(ano, ano - 1);
                V(w.Cell(l, 8), m.Vendas(ano, ac) is decimal x1 && m.Vendas(ano - 1, ac) is decimal x0 && x0 > 0 ? (double)((x1 - x0) / x0 * 100) : null, FmtPct);
                l++;
            }
            w.Columns(1, 8).Width = 20;
        }

        // 6. funil
        if (o.Funil)
        {
            var w = Aba("Funil", "Funil por status (propostas por ano de envio; status de hoje)");
            Cab(w, 4, "Ano", "Total (linhas)", "Ganhas", "Perdidas", "das quais canceladas", "Abertas", "Não classificadas", "% ganhas", "% perdidas", "% abertas", "R$ ganho (mi)", "R$ perdido (mi)", "R$ aberto (mi)", "Conversão s/ decididas (%)", "Conversão s/ todas (%)");
            l = 5;
            foreach (var ano in m.Anos)
            {
                var i = m.Ano(ano); double t = Math.Max(1, i.Linhas);
                w.Cell(l, 1).Value = ano; V(w.Cell(l, 2), i.Linhas, FmtQ); V(w.Cell(l, 3), i.G, FmtQ); V(w.Cell(l, 4), i.P, FmtQ); V(w.Cell(l, 5), i.Canceladas, FmtQ); V(w.Cell(l, 6), i.A, FmtQ); V(w.Cell(l, 7), i.NC, FmtQ);
                V(w.Cell(l, 8), i.G / t * 100, FmtPct); V(w.Cell(l, 9), i.P / t * 100, FmtPct); V(w.Cell(l, 10), i.A / t * 100, FmtPct);
                V(w.Cell(l, 11), i.VG / 1e6, FmtMi); V(w.Cell(l, 12), i.VP / 1e6, FmtMi); V(w.Cell(l, 13), i.VA / 1e6, FmtMi);
                V(w.Cell(l, 14), i.TaxaQtdDecididas, FmtPct); V(w.Cell(l, 15), i.TaxaQtdTodas, FmtPct);
                l++;
            }
            w.Columns(1, 15).Width = 15;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
