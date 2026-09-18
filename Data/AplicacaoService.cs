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
    public sealed record Coluna(string Letra, string Rotulo, int Indice);

    public sealed record Linha(string Origem, string[] Valores, double ValorBrl, string Vendedor, string Aplicador);

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
    }

    // ---- De-Para de colunas: letra do AFM → letra do NB ---------------------
    // A coluna B do AFM alimenta duas do NB (A e C), conforme passado pela área.
    private static readonly (string Afm, string Nb)[] DePara =
    {
        ("B", "A"), ("B", "C"), ("C", "E"), ("D", "F"), ("F", "H"), ("H", "J"),
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

        var gradeNb = nb is null ? null : Ler(nb, d.Avisos, "NB");
        var gradeAfm = afm is null ? null : Ler(afm, d.Avisos, "AFM");
        if (nb is null) d.Avisos.Add($"Planilha do NB não encontrada em: {NbPath}");
        if (afm is null) d.Avisos.Add($"Planilha do AFM não encontrada em: {AfmPath}");

        // Colunas da tabela = colunas do NB que participam do De-Para, na ordem
        // da planilha, com o cabeçalho do NB (ou, se o NB não veio, o do AFM).
        var letrasNb = DePara.Select(p => p.Nb).Distinct().OrderBy(Idx).ToList();
        var cabNb = gradeNb is { Count: > 0 } ? Cabecalho(gradeNb) : null;
        var cabAfm = gradeAfm is { Count: > 0 } ? Cabecalho(gradeAfm) : null;
        for (var i = 0; i < letrasNb.Count; i++)
        {
            var letra = letrasNb[i];
            var rotulo = cabNb is not null ? Texto(cabNb, Idx(letra)) : "";
            if (rotulo == "" && cabAfm is not null)
            {
                var deAfm = DePara.FirstOrDefault(p => p.Nb == letra).Afm;
                if (deAfm is not null) rotulo = Texto(cabAfm, Idx(deAfm));
            }
            d.Colunas.Add(new Coluna(letra, rotulo == "" ? letra : rotulo, i));
        }
        var pos = d.Colunas.ToDictionary(c => c.Letra, c => c.Indice, StringComparer.Ordinal);
        d.IndiceValor = pos.TryGetValue(ColValorNb, out var iv) ? iv : -1;
        var iVend = pos.TryGetValue(ColVendedorNb, out var a) ? a : -1;
        var iApl = pos.TryGetValue(ColAplicadorNb, out var b) ? b : -1;

        // ---- linhas do NB: cada letra na sua coluna, valor já em real ----------
        if (gradeNb is not null)
        {
            var (_, dados) = Separar(gradeNb);
            foreach (var row in dados)
            {
                var v = new string[d.Colunas.Count];
                foreach (var c in d.Colunas) v[c.Indice] = Texto(row, Idx(c.Letra));
                if (v.All(string.IsNullOrWhiteSpace)) continue;
                var valor = Numero(row, Idx(ColValorNb));
                if (d.IndiceValor >= 0) v[d.IndiceValor] = valor.ToString("0.##", Inv);
                d.Linhas.Add(new Linha("NB", v, valor,
                    iVend >= 0 ? v[iVend] : "", iApl >= 0 ? v[iApl] : ""));
                d.LinhasNb++;
            }
        }

        // ---- linhas do AFM: passam pelo De-Para, nomes e câmbio ----------------
        if (gradeAfm is not null)
        {
            var (_, dados) = Separar(gradeAfm);
            foreach (var row in dados)
            {
                var v = new string[d.Colunas.Count];
                foreach (var (colAfm, colNb) in DePara)
                    if (pos.TryGetValue(colNb, out var i)) v[i] = Texto(row, Idx(colAfm));
                if (v.All(string.IsNullOrWhiteSpace)) continue;

                if (iVend >= 0) v[iVend] = Equivalente(v[iVend], VendedorNb);
                if (iApl >= 0) v[iApl] = Equivalente(v[iApl], AplicadorNb);

                var usd = Numero(row, Idx(ColValorAfm));
                var brl = d.TaxaUsd > 0 ? usd * d.TaxaUsd : 0;
                if (d.IndiceValor >= 0) v[d.IndiceValor] = brl.ToString("0.##", Inv);
                d.Linhas.Add(new Linha("AFM", v, brl,
                    iVend >= 0 ? v[iVend] : "", iApl >= 0 ? v[iApl] : ""));
                d.LinhasAfm++;
            }
        }
    }

    // ---- utilitários -----------------------------------------------------------

    private static string Equivalente(string nome, Dictionary<string, string> tabela)
    {
        var n = (nome ?? "").Trim();
        if (n == "") return "";
        return tabela.TryGetValue(OpportunityImporter.Normalizar(n), out var nb) ? nb : n;
    }

    private static List<OpportunityImporter.Cel[]>? Ler(string file, List<string> avisos, string rotulo)
    {
        try
        {
            // Cópia em memória: a planilha pode estar aberta no Excel de alguém.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            var grade = OpportunityImporter.LerGrade(Path.GetFileName(file), ms);
            if (grade.Count == 0) avisos.Add($"Planilha do {rotulo} está vazia: {Path.GetFileName(file)}");
            return grade;
        }
        catch (Exception ex)
        {
            avisos.Add($"Não foi possível ler a planilha do {rotulo} ({Path.GetFileName(file)}): {ex.Message}");
            return null;
        }
    }

    // Cabeçalho = primeira linha com pelo menos três células preenchidas; o que
    // vem antes é título/metadado, o que vem depois é dado.
    private static int LinhaCabecalho(List<OpportunityImporter.Cel[]> grade)
    {
        for (var i = 0; i < grade.Count; i++)
            if (grade[i].Count(c => !string.IsNullOrWhiteSpace(c.Text)) >= 3) return i;
        return 0;
    }

    private static OpportunityImporter.Cel[] Cabecalho(List<OpportunityImporter.Cel[]> grade) => grade[LinhaCabecalho(grade)];

    private static (OpportunityImporter.Cel[] Cab, IEnumerable<OpportunityImporter.Cel[]> Dados) Separar(List<OpportunityImporter.Cel[]> grade)
    {
        var h = LinhaCabecalho(grade);
        return (grade[h], grade.Skip(h + 1));
    }

    private static string Texto(OpportunityImporter.Cel[] row, int i) =>
        i >= 0 && i < row.Length ? (row[i].Text ?? "").Trim() : "";

    private static double Numero(OpportunityImporter.Cel[] row, int i)
    {
        if (i < 0 || i >= row.Length) return 0;
        if (row[i].Num is { } n) return n;
        var t = (row[i].Text ?? "").Trim().Replace("R$", "").Replace("US$", "").Replace("$", "").Trim();
        if (t == "") return 0;
        // "1.234,56" (pt-BR) ou "1,234.56" (en): decide pelo último separador.
        var ultVirg = t.LastIndexOf(','); var ultPonto = t.LastIndexOf('.');
        t = ultVirg > ultPonto ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        return double.TryParse(t, NumberStyles.Any, Inv, out var v) ? v : 0;
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
