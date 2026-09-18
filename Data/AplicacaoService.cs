using System.Globalization;
using Microsoft.Extensions.Configuration;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Guia Aplicação · Tabela de dados.
//
// Duas planilhas na rede — a de aplicadores do Aftermarket (AFM) e a do New
// Business (NB), uma por pasta — viram uma tabela só. A planilha do NB é a
// referência: as colunas da tabela são as colunas dela (pela letra), com o
// cabeçalho que ela traz; cada linha do AFM entra nessas colunas pelo De-Para
// de letras combinado com a área (coluna do AFM → coluna do NB).
//
// A planilha do AFM é limpa: cabeçalho na primeira linha, dados em seguida. A
// do NB é exportação do CRM: 13 linhas de metadados e o cabeçalho na linha 14
// (configurável). O que vem antes do cabeçalho é descartado e a leitura para
// na linha de totais/rodapé, se houver.
//
// Os TÍTULOS das colunas da tabela vêm do cabeçalho do AFM, pela equivalência:
// a coluna B do NB se chama como a coluna A do AFM, e assim por diante. Colunas
// sem equivalência não entram. As LETRAS são as do Excel, sem deslocar —
// a coluna A do NB, que ninguém usa, simplesmente não entra na tabela.
//
// Três colunas pedem tratamento: o vendedor (K do AFM → M do NB) e o aplicador
// (AB → AD) têm nomes escritos de forma diferente nos dois CRMs e são casados
// por uma tabela de equivalência; o valor do AFM (N) vem em dólar e é
// convertido para real pela taxa USD do sistema — o do NB (P) já vem em real.
//
// Leitura tolerante: qualquer problema vira aviso na tela, nunca exceção. O
// resultado fica em memória e só é relido quando algum dos arquivos muda (ou
// quando a pessoa pede).
// ---------------------------------------------------------------------------
public sealed class AplicacaoService
{
    /// <summary>Letra = coluna no NB; LetraAfm = coluna equivalente no AFM (de onde vem o título).</summary>
    public sealed record Coluna(string Letra, string LetraAfm, string Rotulo, int Indice);

    /// <summary>Uma linha da tabela. Valores = células nas colunas da tabela; os
    /// demais campos são as colunas que os gráficos usam, já interpretadas.</summary>
    public sealed record Linha(string Origem, string[] Valores, double ValorBrl, string Vendedor, string Aplicador)
    {
        public DateTime? Actual { get; init; }     // data em que a proposta foi enviada
        public DateTime? Due { get; init; }        // data em que o cliente esperava receber
        public string Industry { get; init; } = "";
        public string Bu { get; init; } = "";
        public double? Gm { get; init; }           // em %, já multiplicado por 100 quando vem decimal
        public string Country { get; init; } = "";
        public string Category { get; init; } = "";
        public string Chave { get; init; } = "";   // primeira coluna preenchida: identifica a proposta nas listas
    }

    public sealed class Dados
    {
        public List<Coluna> Colunas { get; } = new();
        public List<Linha> Linhas { get; } = new();
        public List<string> Avisos { get; } = new();
        public string ArquivoAfm { get; set; } = "";
        public string ArquivoNb { get; set; } = "";
        public int LinhasAfm { get; set; }
        public int LinhasNb { get; set; }
        public double TaxaUsd { get; set; }
        public DateTime? LidoEm { get; set; }
        /// <summary>Posição da coluna de valor (P do NB) em Valores; -1 se não veio.</summary>
        public int IndiceValor { get; set; } = -1;
        /// <summary>Colunas que os gráficos usam e não foram achadas pelo título.</summary>
        public List<string> ColunasFaltando { get; } = new();
    }

    // ---- colunas que os gráficos usam, achadas pelo TÍTULO (cabeçalho do AFM) ----
    // Cada entrada: nome amigável + títulos aceitos (normalizados, por prefixo).
    private static readonly (string Nome, string[] Titulos)[] ColunasGrafico =
    {
        ("Actual",           new[] { "actual" }),
        ("Due",              new[] { "due" }),
        ("ProposalEngineer", new[] { "proposalengineer", "proposal engineer", "aplicador" }),
        ("Salesperson",      new[] { "salesperson", "sales person", "vendedor" }),
        ("Industry",         new[] { "industry", "segmento" }),
        ("BU",               new[] { "bu", "business unit", "unidade" }),
        ("GM",               new[] { "gm", "margem" }),
        ("Country",          new[] { "country", "pais", "país" }),
        ("Category",         new[] { "category", "categoria" }),
    };

    private static int AcharColuna(List<Coluna> colunas, string nome)
    {
        var titulos = ColunasGrafico.First(c => c.Nome == nome).Titulos;
        foreach (var c in colunas)
        {
            var r = OpportunityImporter.Normalizar(c.Rotulo);
            if (titulos.Any(t => r == t)) return c.Indice;
        }
        foreach (var c in colunas)
        {
            var r = OpportunityImporter.Normalizar(c.Rotulo);
            if (titulos.Any(t => r.StartsWith(t + " ", StringComparison.Ordinal) || r.StartsWith(t, StringComparison.Ordinal) && t.Length >= 4)) return c.Indice;
        }
        return -1;
    }

    // ---- De-Para de colunas: letra do AFM → letra do NB ---------------------
    // Letras do Excel, como a área passou. A coluna A do NB não entra: ninguém
    // a usa e nenhuma coluna do AFM aponta para ela.
    private static readonly (string Afm, string Nb)[] DePara =
    {
        ("A", "B"), ("B", "D"), ("C", "E"), ("D", "F"), ("F", "H"), ("H", "J"),
        ("I", "K"), ("J", "L"), ("K", "M"), ("L", "N"), ("M", "O"), ("N", "P"),
        ("O", "Q"), ("Q", "R"), ("S", "T"), ("T", "X"), ("V", "Z"), ("AB", "AD"),
        ("Y", "AB"), ("AA", "AC"), ("W", "AA"),
    };

    private const string ColVendedorNb = "M", ColAplicadorNb = "AD", ColValorNb = "P";
    private const string ColValorAfm = "N";   // em dólar

    // ---- equivalência de nomes (AFM → NB); quem não está aqui passa igual ----
    private static readonly (string Afm, string Nb)[] Vendedores =
    {
        ("Andre Luis de Carvalho",  "Andre Carvalho"),
        ("Bruno Castro",            "Bruno Castro"),
        ("Elmer Calle Chumacero",   "Elmer Calle Chumacero"),
        ("Emerson Barbosa",         "Emerson Barbosa"),
        ("José Ovidio de Moura",    "Jose Moura"),
        ("Leonardo Macachero",      "Leonardo Macachero"),
        ("Manuel Gutierrez",        "Manuel Gutierrez"),
        ("Paulo Sergio Agostinho",  "Paulo Agostinho"),
        ("Rafael Ribeiro Toledo",   "Rafael Toledo"),
        ("Rodrigo Ugas",            "Rodrigo Ugas"),
        ("Stephanie Cipriani",      "Stephanie Cipriani"),
        ("Thiago Cesar Veiga",      "Thiago Veiga"),
    };

    private static readonly (string Afm, string Nb)[] Aplicadores =
    {
        ("Joao Pagliarini",       "Joao Pagliarini"),
        ("Paulo Ricardo Miranda", "Paulo Miranda"),
    };

    private static readonly Dictionary<string, string> VendedorNb =
        Vendedores.ToDictionary(p => OpportunityImporter.Normalizar(p.Afm), p => p.Nb, StringComparer.Ordinal);
    private static readonly Dictionary<string, string> AplicadorNb =
        Aplicadores.ToDictionary(p => OpportunityImporter.Normalizar(p.Afm), p => p.Nb, StringComparer.Ordinal);

    private static readonly string[] Exts = { ".xlsx", ".xlsm", ".xls", ".csv" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IConfiguration _cfg;
    private readonly ControleRepository _controle;
    private readonly object _lock = new();
    private Dados? _cache;
    private string _marca = "";

    public AplicacaoService(IConfiguration cfg, ControleRepository controle)
    {
        _cfg = cfg; _controle = controle;
    }

    public string AfmPath => _cfg["Aplicadores:AfmPath"] ?? "";
    public string NbPath => _cfg["Aplicadores:NbPath"] ?? "";
    /// <summary>Linha (1 = primeira) em que está o cabeçalho; 0 = descobrir sozinho.</summary>
    public int NbHeaderRow => _cfg.GetValue("Aplicadores:NbHeaderRow", 14);
    public int AfmHeaderRow => _cfg.GetValue("Aplicadores:AfmHeaderRow", 1);

    /// <summary>Tabela unificada. Relê os arquivos só quando mudaram na rede
    /// (ou quando <paramref name="force"/>); fora isso devolve o que está em memória.</summary>
    public Dados Carregar(bool force = false)
    {
        lock (_lock)
        {
            var afm = ResolveFile(AfmPath);
            var nb = ResolveFile(NbPath);
            var marca = Marca(afm) + "|" + Marca(nb);
            if (!force && _cache is not null && marca == _marca) return _cache;

            var d = new Dados { ArquivoAfm = afm ?? "", ArquivoNb = nb ?? "", LidoEm = DateTime.Now };
            try { Montar(d, afm, nb); }
            catch (Exception ex) { d.Avisos.Add("Falha ao montar a tabela: " + ex.Message); }
            _cache = d; _marca = marca;
            return d;
        }
    }

    private void Montar(Dados d, string? afm, string? nb)
    {
        // Taxa USD → BRL do sistema (aba Controle). Sem taxa, o valor do AFM fica
        // zerado e a tela avisa — melhor do que inventar uma cotação.
        d.TaxaUsd = _controle.CurrencyRates()
            .FirstOrDefault(m => m.Code.Equals("USD", StringComparison.OrdinalIgnoreCase) && m.HasRate)?.RateV
            ?? Opportunity.BrlPerUsd;
        if (d.TaxaUsd <= 0) d.Avisos.Add("Sem taxa USD cadastrada no Controle: os valores do AFM ficaram zerados.");

        var planNb = nb is null ? null : Ler(nb, d.Avisos, "NB", NbHeaderRow);
        var planAfm = afm is null ? null : Ler(afm, d.Avisos, "AFM", AfmHeaderRow);
        if (nb is null) d.Avisos.Add($"Planilha do NB não encontrada em: {NbPath}");
        if (afm is null) d.Avisos.Add($"Planilha do AFM não encontrada em: {AfmPath}");

        // Colunas da tabela = colunas do NB que participam do De-Para, na ordem
        // da planilha. O título vem do cabeçalho do AFM (coluna equivalente); se
        // o AFM não veio, do NB; em último caso, a própria letra.
        var letrasNb = DePara.Select(p => p.Nb).Distinct().OrderBy(Idx).ToList();
        for (var i = 0; i < letrasNb.Count; i++)
        {
            var letra = letrasNb[i];
            var deAfm = DePara.First(p => p.Nb == letra).Afm;
            var rotulo = planAfm is not null ? planAfm.Texto(planAfm.Cab, deAfm) : "";
            if (rotulo == "" && planNb is not null) rotulo = planNb.Texto(planNb.Cab, letra);
            d.Colunas.Add(new Coluna(letra, deAfm, rotulo == "" ? letra : rotulo, i));
        }
        var pos = d.Colunas.ToDictionary(c => c.Letra, c => c.Indice, StringComparer.Ordinal);
        d.IndiceValor = pos.TryGetValue(ColValorNb, out var iv) ? iv : -1;
        // A coluna de valor ganha o nome pedido pela área.
        if (d.IndiceValor >= 0) d.Colunas[d.IndiceValor] = d.Colunas[d.IndiceValor] with { Rotulo = "Valor TOTAL" };
        var iVend = pos.TryGetValue(ColVendedorNb, out var a) ? a : -1;
        var iApl = pos.TryGetValue(ColAplicadorNb, out var b) ? b : -1;

        // Colunas dos gráficos, pelo título. As de vendedor e aplicador têm a
        // letra combinada como reserva, se o título não casar.
        var iActual = AcharColuna(d.Colunas, "Actual");
        var iDue = AcharColuna(d.Colunas, "Due");
        var iEng = AcharColuna(d.Colunas, "ProposalEngineer"); if (iEng < 0) iEng = iApl;
        var iSales = AcharColuna(d.Colunas, "Salesperson"); if (iSales < 0) iSales = iVend;
        var iInd = AcharColuna(d.Colunas, "Industry");
        var iBu = AcharColuna(d.Colunas, "BU");
        var iGm = AcharColuna(d.Colunas, "GM");
        var iPais = AcharColuna(d.Colunas, "Country");
        var iCat = AcharColuna(d.Colunas, "Category");
        foreach (var (nome, i) in new[] { ("Actual", iActual), ("Due", iDue), ("ProposalEngineer", iEng), ("Salesperson", iSales),
                                          ("Industry", iInd), ("BU", iBu), ("GM", iGm), ("Country", iPais), ("Category", iCat) })
            if (i < 0) d.ColunasFaltando.Add(nome);

        string Cel(string[] v, int i) => i >= 0 && i < v.Length ? (v[i] ?? "").Trim() : "";
        Linha Completar(Linha l, bool nb)
        {
            var v = l.Valores;
            var gm = Percentual(Cel(v, iGm));
            if (gm is { } g && nb) gm = g * 100;     // o NB traz decimal (0,25 = 25%)
            return l with
            {
                Actual = Data(Cel(v, iActual)),
                Due = Data(Cel(v, iDue)),
                Industry = Cel(v, iInd), Bu = Cel(v, iBu), Gm = gm,
                Country = Cel(v, iPais), Category = Cel(v, iCat),
                Chave = v.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
                Aplicador = iEng >= 0 ? Cel(v, iEng) : l.Aplicador,
                Vendedor = iSales >= 0 ? Cel(v, iSales) : l.Vendedor,
            };
        }

        // ---- linhas do NB: cada letra na sua coluna, valor já em real ----------
        if (planNb is not null)
        {
            foreach (var row in planNb.Dados)
            {
                var v = new string[d.Colunas.Count];
                foreach (var c in d.Colunas) v[c.Indice] = planNb.Texto(row, c.Letra);
                if (v.All(string.IsNullOrWhiteSpace)) continue;
                var valor = planNb.Numero(row, ColValorNb);
                if (d.IndiceValor >= 0) v[d.IndiceValor] = valor.ToString("0.##", Inv);
                d.Linhas.Add(Completar(new Linha("NB", v, valor,
                    iVend >= 0 ? v[iVend] : "", iApl >= 0 ? v[iApl] : ""), nb: true));
                d.LinhasNb++;
            }
        }

        // ---- linhas do AFM: passam pelo De-Para, nomes e câmbio ----------------
        if (planAfm is not null)
        {
            foreach (var row in planAfm.Dados)
            {
                var v = new string[d.Colunas.Count];
                foreach (var (colAfm, colNb) in DePara)
                    if (pos.TryGetValue(colNb, out var i)) v[i] = planAfm.Texto(row, colAfm);
                if (v.All(string.IsNullOrWhiteSpace)) continue;

                if (iVend >= 0) v[iVend] = Equivalente(v[iVend], VendedorNb);
                if (iApl >= 0) v[iApl] = Equivalente(v[iApl], AplicadorNb);

                var usd = planAfm.Numero(row, ColValorAfm);
                var brl = d.TaxaUsd > 0 ? usd * d.TaxaUsd : 0;
                if (d.IndiceValor >= 0) v[d.IndiceValor] = brl.ToString("0.##", Inv);
                d.Linhas.Add(Completar(new Linha("AFM", v, brl,
                    iVend >= 0 ? v[iVend] : "", iApl >= 0 ? v[iApl] : ""), nb: false));
                d.LinhasAfm++;
            }
        }
    }

    // ---- planilha já preparada -------------------------------------------------
    // Cab = linha de cabeçalho; Dados = só as linhas de dados (sem título em cima,
    // sem totais embaixo, sem linhas vazias). As letras são as do Excel.
    private sealed class Planilha
    {
        public OpportunityImporter.Cel[] Cab { get; init; } = Array.Empty<OpportunityImporter.Cel>();
        public List<OpportunityImporter.Cel[]> Dados { get; init; } = new();

        public string Texto(OpportunityImporter.Cel[] row, string letra)
        {
            var i = Idx(letra);
            return i >= 0 && i < row.Length ? (row[i].Text ?? "").Trim() : "";
        }

        public double Numero(OpportunityImporter.Cel[] row, string letra)
        {
            var i = Idx(letra);
            if (i < 0 || i >= row.Length) return 0;
            if (row[i].Num is { } n) return n;
            var t = (row[i].Text ?? "").Trim().Replace("R$", "").Replace("US$", "").Replace("$", "").Trim();
            if (t == "") return 0;
            // "1.234,56" (pt-BR) ou "1,234.56" (en): decide pelo último separador.
            var ultVirg = t.LastIndexOf(','); var ultPonto = t.LastIndexOf('.');
            t = ultVirg > ultPonto ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
            return double.TryParse(t, NumberStyles.Any, Inv, out var v) ? v : 0;
        }
    }

    private static Planilha Preparar(List<OpportunityImporter.Cel[]> grade, int linhaCabecalho)
    {
        static int Cheias(OpportunityImporter.Cel[] r) => r.Count(c => !string.IsNullOrWhiteSpace(c.Text));

        int h;
        if (linhaCabecalho > 0)
        {
            // Linha informada pela área (1 = primeira linha da planilha).
            h = Math.Min(linhaCabecalho - 1, Math.Max(grade.Count - 1, 0));
        }
        else
        {
            // Sem linha informada: a primeira linha "cheia" — pelo menos três
            // células e ao menos metade da linha mais cheia da planilha. Título e
            // data em cima têm uma ou duas células e ficam de fora.
            var max = grade.Count == 0 ? 0 : grade.Max(Cheias);
            h = 0;
            for (var i = 0; i < grade.Count; i++)
                if (Cheias(grade[i]) >= 3 && Cheias(grade[i]) * 2 >= max) { h = i; break; }
        }

        var dados = new List<OpportunityImporter.Cel[]>();
        foreach (var row in grade.Skip(h + 1))
        {
            if (Cheias(row) == 0) continue;
            if (EhTotal(row)) break;          // rodapé da exportação: daqui para baixo nada é dado
            dados.Add(row);
        }

        return new Planilha { Cab = grade.Count > h ? grade[h] : Array.Empty<OpportunityImporter.Cel>(), Dados = dados };
    }

    // Linha de totais / rodapé da exportação (mesmos sinais da importação).
    private static bool EhTotal(OpportunityImporter.Cel[] row)
    {
        foreach (var c in row)
        {
            var n = OpportunityImporter.Normalizar(c.Text ?? "");
            if (n == "") continue;
            if (n is "total" or "sum" or "count" or "grand total" or "subtotal" or "totais") return true;
            if (n.Contains("confidential information") || n.Contains("do not distribute")
                || n.Contains("salesforce.com") || n.Contains("copyright")) return true;
            return false;                    // a primeira célula preenchida decide
        }
        return false;
    }

    // ---- utilitários -----------------------------------------------------------

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Data em qualquer formato que as planilhas trazem: ISO, dd/MM/aaaa,
    /// MM/dd/aaaa (texto do CRM) ou número de série do Excel.</summary>
    public static DateTime? Data(string s)
    {
        s = (s ?? "").Trim();
        if (s == "") return null;
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" }, Inv, DateTimeStyles.None, out var d)) return d.Date;
        if (DateTime.TryParse(s, Br, DateTimeStyles.None, out d)) return d.Date;
        if (DateTime.TryParse(s, Inv, DateTimeStyles.None, out d)) return d.Date;
        if (double.TryParse(s, NumberStyles.Any, Inv, out var serial) && serial is > 20000 and < 80000) return DateTime.FromOADate(serial).Date;
        return null;
    }

    private static double? Percentual(string s)
    {
        s = (s ?? "").Trim().Replace("%", "").Trim();
        if (s == "") return null;
        var ultVirg = s.LastIndexOf(','); var ultPonto = s.LastIndexOf('.');
        s = ultVirg > ultPonto ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        return double.TryParse(s, NumberStyles.Any, Inv, out var v) ? v : null;
    }

    private static string Equivalente(string nome, Dictionary<string, string> tabela)
    {
        var n = (nome ?? "").Trim();
        if (n == "") return "";
        return tabela.TryGetValue(OpportunityImporter.Normalizar(n), out var nb) ? nb : n;
    }

    private static Planilha? Ler(string file, List<string> avisos, string rotulo, int linhaCabecalho)
    {
        try
        {
            // Cópia em memória: a planilha pode estar aberta no Excel de alguém.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            var grade = OpportunityImporter.LerGrade(Path.GetFileName(file), ms);
            if (grade.Count == 0)
            {
                avisos.Add($"Planilha do {rotulo} está vazia: {Path.GetFileName(file)}");
                return null;
            }
            var plan = Preparar(grade, linhaCabecalho);
            if (plan.Dados.Count == 0) avisos.Add($"Planilha do {rotulo} sem linhas de dados abaixo do cabeçalho: {Path.GetFileName(file)}");
            return plan;
        }
        catch (Exception ex)
        {
            avisos.Add($"Não foi possível ler a planilha do {rotulo} ({Path.GetFileName(file)}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Letra de coluna do Excel → índice (A=0 … Z=25, AA=26, AD=29).</summary>
    public static int Idx(string letra)
    {
        var n = 0;
        foreach (var ch in letra.Trim().ToUpperInvariant())
        {
            if (ch < 'A' || ch > 'Z') return -1;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
    }

    private static string? ResolveFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (File.Exists(path)) return path;
            if (!Directory.Exists(path)) return null;
            return new DirectoryInfo(path).EnumerateFiles()
                .Where(f => Exts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith("~$"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string Marca(string? file)
    {
        if (file is null) return "-";
        try { var fi = new FileInfo(file); return $"{fi.FullName}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}"; }
        catch { return file; }
    }
}
