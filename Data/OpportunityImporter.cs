using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Importador da base de oportunidades a partir de um arquivo Excel (.xlsx) ou
// CSV. Leitura TOLERANTE (nunca lança): cada problema vira um aviso no relatório.
// As colunas seguem exatamente o layout da planilha do forecast (Quarter, Date,
// País, Market Variável, Market, Product, Tipo de Equipamento, Key Account,
// Customer, Proposta, Net Value, PM %, % de Ganho, % de Sair no Mês, Chance
// Conversão, NB/AFM, Serviço previsto, Market onestream, Unidade de Venda,
// BU Intercompany, Observação, PV, RAMP, VALOR USD, Taxa, Coluna1).
// Nomes de país/mercado/cliente etc. são casados com o catálogo; quando não há
// correspondência, o texto original é preservado e ainda aparece na listagem.
// ---------------------------------------------------------------------------
public sealed class OpportunityImporter
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Catalog _cat;
    public OpportunityImporter(Catalog cat) => _cat = cat;

    public sealed class Result
    {
        public List<Opportunity> Rows { get; } = new();
        public List<string> Warnings { get; } = new();
        public int Read { get; set; }
        public int Skipped { get; set; }
        public bool Ok => Rows.Count > 0;
    }

    public Result Parse(string fileName, Stream stream)
    {
        try
        {
            return fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? ParseCsv(stream)
                : ParseXlsx(stream);
        }
        catch (Exception ex)
        {
            var r = new Result();
            r.Warnings.Add("Não foi possível ler o arquivo: " + ex.Message);
            return r;
        }
    }

    // ---- Excel ------------------------------------------------------------
    private Result ParseXlsx(Stream stream)
    {
        var r = new Result();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault();
        if (ws is null) { r.Warnings.Add("A planilha está vazia."); return r; }

        var rows = ws.RowsUsed().ToList();
        if (rows.Count < 2) { r.Warnings.Add("Nenhuma linha de dados encontrada abaixo do cabeçalho."); return r; }

        // Cabeçalho → índice da coluna.
        var header = rows[0];
        var map = new Dictionary<string, int>();
        foreach (var cell in header.CellsUsed())
        {
            var key = Norm(cell.GetString());
            if (key != "" && !map.ContainsKey(key)) map[key] = cell.Address.ColumnNumber;
        }

        foreach (var row in rows.Skip(1))
        {
            string Get(params string[] names)
            {
                foreach (var n in names)
                    if (map.TryGetValue(Norm(n), out var col))
                        return row.Cell(col).GetString().Trim();
                return "";
            }
            BuildRow(r, Get);
        }
        return r;
    }

    // ---- CSV --------------------------------------------------------------
    private Result ParseCsv(Stream stream)
    {
        var r = new Result();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) { r.Warnings.Add("Nenhuma linha de dados encontrada."); return r; }

        var delim = lines[0].Count(c => c == ';') >= lines[0].Count(c => c == ',') ? ';' : ',';
        var headerCells = SplitCsv(lines[0], delim);
        var map = new Dictionary<string, int>();
        for (var i = 0; i < headerCells.Count; i++)
        {
            var key = Norm(headerCells[i]);
            if (key != "" && !map.ContainsKey(key)) map[key] = i;
        }

        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsv(line, delim);
            string Get(params string[] names)
            {
                foreach (var n in names)
                    if (map.TryGetValue(Norm(n), out var i) && i < cells.Count)
                        return cells[i].Trim();
                return "";
            }
            BuildRow(r, Get);
        }
        return r;
    }

    private static List<string> SplitCsv(string line, char delim)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == delim && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ---- Monta uma oportunidade a partir de um acessor de células ----------
    private void BuildRow(Result r, Func<string[], string> get)
    {
        var proposta = get(new[] { "Proposta" });
        var customerRaw = get(new[] { "Customer", "Cliente" });
        var valueRaw = get(new[] { "Net Value", "Valor BRL", "Valor" });

        // Linha totalmente vazia: ignora silenciosamente.
        if (proposta == "" && customerRaw == "" && valueRaw == "") return;
        r.Read++;

        var dateIso = ResolveDate(get(new[] { "Date", "Data", "Data prevista" }), get(new[] { "Quarter" }));
        var country = Match(_cat.Countries, c => c.Name, c => c.Id, get(new[] { "País", "Pais", "Country" }));
        var market = Match(_cat.Markets, m => m.Name, m => m.Id, get(new[] { "Market" }));
        var submarket = Match(_cat.SubMarkets, s => s.Name, s => s.Id, get(new[] { "Market Variável", "Market Variavel", "Sub_Market", "Submercado" }));
        var product = Match(_cat.Products, p => p.Name, p => p.Id, get(new[] { "Product", "Produto" }));
        var equip = Match(_cat.EquipmentTypes, e => e.Name, e => e.Id, get(new[] { "Tipo de Equipamento", "Tipo de equipamento" }));
        var kam = Match(_cat.Kams, k => k.Name, k => k.Id, get(new[] { "Key Account", "KAM" }));
        var customer = Match(_cat.Customers, c => c.Name, c => c.Id, customerRaw);
        var puv = MatchBu(get(new[] { "Unidade de Venda", "BU do PV", "Unidade de venda" }));
        var buInter = MatchBu(get(new[] { "BU Intercompany", "BU  Intercompany" }));

        var netBrl = ParseNum(valueRaw) ?? 0;
        var taxa = ParseNum(get(new[] { "Taxa", "Taxa de câmbio", "Taxa de cambio" })) ?? 0;
        if (taxa <= 0) taxa = _cat.RateToUsd("USD"); // cai no câmbio padrão do catálogo

        var o = new Opportunity
        {
            Id = proposta != "" ? "imp-" + Norm(proposta) : "imp-" + Guid.NewGuid().ToString("N"),
            Name = customerRaw != "" && product != "" ? $"{_cat.ProductName(product)} — {_cat.CustomerName(customer)}"
                 : (proposta != "" ? proposta : "Oportunidade importada"),
            ProposalNumber = proposta,
            PvNumber = get(new[] { "PV" }),
            CountryId = country,
            MarketId = market,
            SubMarketId = submarket,
            ProductId = product,
            EquipmentTypeId = equip,
            KamId = kam,
            CustomerId = customer,
            CommercialCategory = NormalizeCat(get(new[] { "NB/AFM", "NB/RT/AFM/SV" })),
            IntercompanyBu = buInter,
            PvBusinessUnitId = puv,
            CurrencyCode = "BRL",
            AmountOriginal = netBrl.ToString(Inv),
            ExchangeRate = taxa.ToString(Inv),
            GmPercent = Pct(get(new[] { "PM %", "PM%", "GM%", "GM %" })).ToString(Inv),
            ForecastCategory = "Pipeline",
            PipelineStageId = "st-qual",
            ExpectedDate = dateIso,
            WinProbability = Pct(get(new[] { "% de Ganho", "% de ganho" })).ToString(Inv),
            CloseInPeriodProbability = Pct(get(new[] { "% de Sair no Mês", "% de sair no mês", "% de sair no mes" })).ToString(Inv),
            Notes = get(new[] { "Observação", "Observacao", "Observações", "Observacoes" }),
            PostponeCount = "0",
            CreatedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            UpdatedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            UpdatedBy = "Importação",
        };

        if (dateIso == "") r.Warnings.Add($"Proposta {Show(proposta)}: data inválida ou ausente.");
        if (netBrl <= 0) r.Warnings.Add($"Proposta {Show(proposta)}: valor (Net Value) ausente ou zero.");

        r.Rows.Add(o);
    }

    private static string Show(string s) => string.IsNullOrWhiteSpace(s) ? "(sem número)" : s;

    // NB/RT/AFM/SV — normaliza para o código conhecido, senão preserva o texto.
    private static string NormalizeCat(string s)
    {
        var n = Norm(s);
        return n switch
        {
            "nb" or "new business" => "NB",
            "rt" or "retrofit" => "RT",
            "afm" or "aftermarket" => "AFM",
            "sv" or "service" or "servico" or "serviço" => "SV",
            _ => s.Trim(),
        };
    }

    private string MatchBu(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return "";
        var hit = _cat.BusinessUnits.FirstOrDefault(b => Norm(b.Code) == Norm(val) || Norm(b.Name) == Norm(val));
        return hit?.Id ?? val.Trim();
    }

    private static string Match<T>(IEnumerable<T> list, Func<T, string> name, Func<T, string> id, string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return "";
        var hit = list.FirstOrDefault(x => Norm(name(x)) == Norm(val));
        return hit != null ? id(hit) : val.Trim();
    }

    // ---- parsers tolerantes -----------------------------------------------
    private static double? ParseNum(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("R$", "").Replace("US$", "").Replace("%", "").Replace(" ", " ").Trim();
        if (s == "") return null;
        if (double.TryParse(s, NumberStyles.Any, Br, out var v)) return v;
        if (double.TryParse(s, NumberStyles.Any, Inv, out v)) return v;
        return null;
    }

    private static double Pct(string raw)
    {
        var hadPct = raw.Contains('%');
        var n = ParseNum(raw) ?? 0;
        if (!hadPct && n > 0 && n <= 1) n *= 100; // 0,94 → 94
        return Math.Clamp(n, 0, 100);
    }

    // A coluna "Date" da planilha é uma REFERÊNCIA DE MÊS (número 1–12), não um
    // dia específico. Resolvemos para o 1º dia do mês; o ano vem da coluna
    // Quarter (ex.: "Q3 2026") ou, na falta dele, do ano corrente.
    private static string ResolveDate(string raw, string quarterRaw)
    {
        raw = raw.Trim();
        if (raw == "") return "";

        // Data completa (dd/mm/aaaa, ISO ou serial do Excel).
        var full = IsoDate(raw);
        if (full != "") return full;

        // Número do mês (1–12) → 1º dia do mês, com o ano da coluna Quarter.
        if (double.TryParse(raw, NumberStyles.Any, Inv, out var n) ||
            double.TryParse(raw, NumberStyles.Any, Br, out n))
        {
            var month = (int)Math.Round(n);
            if (month is >= 1 and <= 12)
            {
                var year = YearFrom(quarterRaw) ?? DateTime.Today.Year;
                return new DateTime(year, month, 1).ToString("yyyy-MM-dd");
            }
        }
        return "";
    }

    // Extrai um ano de quatro dígitos de um texto (ex.: "Q3 2026" → 2026).
    private static int? YearFrom(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var digits = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsDigit(c)) { digits.Append(c); if (digits.Length == 4 && int.Parse(digits.ToString()) is >= 2000 and <= 2100) return int.Parse(digits.ToString()); }
            else if (digits.Length is > 0 and < 4) digits.Clear();
        }
        return null;
    }

    private static string IsoDate(string raw)
    {
        raw = raw.Trim();
        if (raw == "") return "";
        // Um número curto (1–12) NÃO é uma data completa — deixa para ResolveDate.
        if (double.TryParse(raw, NumberStyles.Any, Inv, out var small) && small is >= 1 and <= 12 && raw.Length <= 2)
            return "";
        if (DateTime.TryParse(raw, Br, DateTimeStyles.None, out var d)) return d.ToString("yyyy-MM-dd");
        if (DateTime.TryParse(raw, Inv, DateTimeStyles.None, out d)) return d.ToString("yyyy-MM-dd");
        if (double.TryParse(raw, NumberStyles.Any, Inv, out var serial) && serial is > 20000 and < 80000)
            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd");
        return "";
    }

    // Normalização para casar nomes: minúsculas, sem acentos, espaços colapsados.
    private static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return string.Join(' ', sb.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
