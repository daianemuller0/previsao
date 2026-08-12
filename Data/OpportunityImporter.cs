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

    // Cabeçalhos conhecidos (novo layout Funil HSA + legado) para localizar a linha
    // de cabeçalho da planilha, que não é a primeira (há metadados acima).
    private static readonly HashSet<string> KnownHeaders = new(new[]
    {
        // Funil de Vendas HSA
        "close date", "installation location", "parent opportunity: end user site", "commercial segment",
        "market", "sub-market", "process", "product type", "brand", "outside sales rep", "account name",
        "opportunity number", "amount", "amount (converted)", "gross margin(%)", "stage", "probability (%)",
        "chance", "customer ref#", "business unit", "is inter company", "opportunity name",
        "status comments", "description", "status description",
        // Layout legado
        "net value", "proposta", "customer", "quarter", "date", "product", "kam", "key account",
        "market variavel", "valor", "pm %",
        // Aftermarket (novo CRM) — nomes de origem do De-Para de Configurações
        "po esperado", "country", "plantname", "crs_market", "industry", "subindustry",
        "producttype", "contractorname", "quotenumber", "moeda", "gm", "funnelstage",
        "clientref", "bu", "addtoforecast", "special", "category",
    }.Select(Norm));

    // Origem dos dados: New Business (planilha Funil HSA) ou Aftermarket (novo CRM).
    public enum Source { Nb, Afm }

    private readonly Catalog _cat;
    public OpportunityImporter(Catalog cat) => _cat = cat;

    public sealed class Result
    {
        public List<Opportunity> Rows { get; } = new();
        public List<string> Warnings { get; } = new();
        public int Read { get; set; }
        public int Skipped { get; set; }
        public bool Ok => Rows.Count > 0;
        // Conta ocorrências de cada id-base no arquivo. A base é consolidada por id
        // (um registro por id), então propostas repetidas na planilha precisam de
        // ids distintos para não colapsarem e some do total.
        internal Dictionary<string, int> IdSeq { get; } = new();
    }

    public Result Parse(string fileName, Stream stream,
        Source source = Source.Nb, Func<string, double?>? rate = null, Action<string>? onCurrency = null)
    {
        try
        {
            return fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? ParseCsv(stream, source, rate, onCurrency)
                : ParseXlsx(stream, source, rate, onCurrency);
        }
        catch (Exception ex)
        {
            var r = new Result();
            r.Warnings.Add("Não foi possível ler o arquivo: " + ex.Message);
            return r;
        }
    }

    // ---- Excel ------------------------------------------------------------
    private Result ParseXlsx(Stream stream, Source source, Func<string, double?>? rate, Action<string>? onCurrency)
    {
        var r = new Result();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault();
        if (ws is null) { r.Warnings.Add("A planilha está vazia."); return r; }

        var rows = ws.RowsUsed().ToList();
        if (rows.Count < 2) { r.Warnings.Add("Nenhuma linha de dados encontrada abaixo do cabeçalho."); return r; }

        // Acha a linha de CABEÇALHO (a planilha do Funil HSA tem 14 linhas de
        // metadados antes do cabeçalho real). Escolhe a linha com mais colunas
        // conhecidas; ignora colunas em branco (coluna A e a vazia intermediária).
        int hi = 0, best = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var hits = rows[i].CellsUsed().Count(c => KnownHeaders.Contains(Norm(c.GetString())));
            if (hits > best) { best = hits; hi = i; }
        }

        var header = rows[hi];
        var map = new Dictionary<string, int>();
        foreach (var cell in header.CellsUsed())
        {
            var key = Norm(cell.GetString());
            if (key != "" && !map.ContainsKey(key)) map[key] = cell.Address.ColumnNumber;
        }

        foreach (var row in rows.Skip(hi + 1))
        {
            string Get(params string[] names)
            {
                foreach (var n in names)
                    if (map.TryGetValue(Norm(n), out var col))
                        return row.Cell(col).GetString().Trim();
                return "";
            }
            // Lê o VALOR NUMÉRICO direto da célula quando ela é numérica — evita a
            // ambiguidade de separador (ponto/vírgula) ao converter para texto.
            double? GetN(params string[] names)
            {
                foreach (var n in names)
                    if (map.TryGetValue(Norm(n), out var col))
                    {
                        var cell = row.Cell(col);
                        return cell.Value.IsNumber ? cell.Value.GetNumber() : (double?)null;
                    }
                return null;
            }
            // Região de TOTAIS/rodapé no fim da planilha (posição varia): dessa
            // linha para baixo nada é importado.
            if (IsStopRow(row.CellsUsed().Select(c => c.GetString()), Get)) break;
            if (source == Source.Afm) BuildAfmRow(r, Get, GetN, rate, onCurrency);
            else BuildRow(r, Get, GetN);
        }
        return r;
    }

    // ---- CSV --------------------------------------------------------------
    private Result ParseCsv(Stream stream, Source source, Func<string, double?>? rate, Action<string>? onCurrency)
    {
        var r = new Result();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) { r.Warnings.Add("Nenhuma linha de dados encontrada."); return r; }

        var delim = lines[0].Count(c => c == ';') >= lines[0].Count(c => c == ',') ? ';' : ',';

        // Acha a linha de cabeçalho (mais colunas conhecidas), ignorando metadados.
        int hi = 0, best = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var hits = SplitCsv(lines[i], delim).Count(c => KnownHeaders.Contains(Norm(c)));
            if (hits > best) { best = hits; hi = i; }
        }

        var headerCells = SplitCsv(lines[hi], delim);
        var map = new Dictionary<string, int>();
        for (var i = 0; i < headerCells.Count; i++)
        {
            var key = Norm(headerCells[i]);
            if (key != "" && !map.ContainsKey(key)) map[key] = i;
        }

        foreach (var line in lines.Skip(hi + 1))
        {
            var cells = SplitCsv(line, delim);
            string Get(params string[] names)
            {
                foreach (var n in names)
                    if (map.TryGetValue(Norm(n), out var i) && i < cells.Count)
                        return cells[i].Trim();
                return "";
            }
            if (IsStopRow(cells, Get)) break;
            if (source == Source.Afm) BuildAfmRow(r, Get, _ => null, rate, onCurrency);
            else BuildRow(r, Get, _ => null);
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

    // Detecta o início da região de TOTAIS/rodapé (Salesforce): a última parte da
    // planilha traz linhas de total ("Total", "Sum", "Count"), a linha de grande
    // total (valor cheio, sem proposta nem cliente) e o rodapé "Confidential /
    // Copyright / salesforce.com". A posição varia conforme a quantidade de dados;
    // ao encontrar a primeira, paramos e nada abaixo é importado.
    private static bool IsStopRow(IEnumerable<string> cells, Func<string[], string> get)
    {
        foreach (var c in cells)
        {
            var n = Norm(c);
            if (n is "total" or "sum" or "count" or "grand total" or "subtotal" or "totais") return true;
            if (n.Contains("confidential information") || n.Contains("do not distribute")
                || n.Contains("salesforce.com") || n.Contains("copyright")) return true;
        }
        // Linha de grande total: tem valor, mas sem identificadores (proposta/cliente).
        var prop = get(new[] { "Opportunity Number", "Proposta", "QuoteNumber" });
        var acc = get(new[] { "Account Name", "Customer", "Cliente", "PlantName", "ContractorName" });
        if (prop == "" && acc == "")
        {
            var val = getNumStub(get, "Amount (converted)", "Net Value", "Valor", "Amount");
            if (val != 0) return true;
        }
        return false;
    }

    // Valor numérico (texto) de um dos campos, para a detecção de linha de total.
    private static double getNumStub(Func<string[], string> get, params string[] names)
        => ParseNum(get(names)) ?? 0;

    // ---- Monta uma oportunidade a partir de um acessor de células ----------
    private void BuildRow(Result r, Func<string[], string> get, Func<string[], double?> getNum)
    {
        // Número de um campo: usa o valor numérico da célula (Excel) e, na falta,
        // o texto interpretado (CSV). Evita erro de separador de milhar/decimal.
        double? Num(params string[] names) => getNum(names) ?? ParseNum(get(names));
        double PctOf(params string[] names) => PctField(getNum(names), get(names));

        // Nº da oportunidade (novo: "Opportunity Number"; legado: "Proposta").
        var proposta = get(new[] { "Opportunity Number", "Proposta" });
        var customerRaw = get(new[] { "Account Name", "Customer", "Cliente" });
        var valueRaw = get(new[] { "Amount (converted)", "Net Value", "Valor BRL", "Valor" });

        // Linha totalmente vazia: ignora silenciosamente.
        if (proposta == "" && customerRaw == "" && valueRaw == "") return;
        r.Read++;

        // Close Date (novo) é data completa; Date/Quarter (legado) é mês+ano.
        var dateIso = ResolveDate(get(new[] { "Close Date", "Date", "Data", "Data prevista" }), get(new[] { "Quarter" }));
        // Installation Location = país do mapa.
        var country = Match(_cat.Countries, c => c.Name, c => c.Id, get(new[] { "Installation Location", "País", "Pais", "Country" }));
        var market = Match(_cat.Markets, m => m.Name, m => m.Id, get(new[] { "Market" }));
        var submarket = Match(_cat.SubMarkets, s => s.Name, s => s.Id, get(new[] { "Sub-Market", "Market Variável", "Market Variavel", "Sub_Market", "Submercado" }));
        var product = Match(_cat.Products, p => p.Name, p => p.Id, get(new[] { "Product Type", "Product", "Produto" }));
        var equip = Match(_cat.EquipmentTypes, e => e.Name, e => e.Id, get(new[] { "Tipo de Equipamento", "Tipo de equipamento" }));
        // Outside Sales Rep = "Vendedor" (reaproveita a dimensão KAM).
        var kam = Match(_cat.Kams, k => k.Name, k => k.Id, get(new[] { "Outside Sales Rep", "Key Account", "KAM" }));
        var customer = Match(_cat.Customers, c => c.Name, c => c.Id, customerRaw);
        var puv = MatchBu(get(new[] { "Business Unit", "Unidade de Venda", "BU do PV", "Unidade de venda" }));
        var buInter = MatchBu(get(new[] { "BU Intercompany", "BU  Intercompany" }));

        // Amount (converted) já vem em R$ (decisão do projeto) → sem conversão (taxa 1).
        var netBrl = Num("Amount (converted)", "Net Value", "Valor BRL", "Valor") ?? 0;
        var taxa = Num("Taxa", "Taxa de câmbio", "Taxa de cambio") ?? 1;
        if (taxa <= 0) taxa = 1;

        // Id-base pela proposta (permite reimportar/atualizar sem duplicar). Quando
        // a MESMA proposta aparece em várias linhas da planilha, cada ocorrência
        // recebe um sufixo para não colapsar na consolidação por id.
        var baseId = proposta != "" ? "imp-" + Norm(proposta) : "imp-" + Guid.NewGuid().ToString("N");
        var seq = r.IdSeq.TryGetValue(baseId, out var nseq) ? nseq + 1 : 1;
        r.IdSeq[baseId] = seq;
        if (seq == 2 && proposta != "")
            r.Warnings.Add($"Proposta {Show(proposta)} aparece em mais de uma linha; linhas mantidas separadamente.");
        var id = seq == 1 ? baseId : baseId + "-" + seq.ToString(Inv);

        var oppName = get(new[] { "Opportunity Name" });
        var o = new Opportunity
        {
            Id = id,
            Name = oppName != "" ? oppName
                 : (customerRaw != "" && product != "" ? $"{_cat.ProductName(product)} — {_cat.CustomerName(customer)}"
                 : (proposta != "" ? proposta : "Oportunidade importada")),
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
            ServicoPrevisto = get(new[] { "Serviço previsto", "Servico previsto" }),
            MarketOnestream = get(new[] { "Market onestream", "Market Onestream" }),
            Ramp = get(new[] { "RAMP", "Ramp" }),
            Coluna1 = get(new[] { "Coluna1" }),
            CurrencyCode = "BRL",
            AmountOriginal = netBrl.ToString(Inv),
            ExchangeRate = taxa.ToString(Inv),
            GmPercent = PctOf("Gross Margin(%)", "Gross Margin (%)", "PM %", "PM%", "GM%", "GM %").ToString(Inv),
            ForecastCategory = "Pipeline",
            PipelineStageId = "st-qual",
            ExpectedDate = dateIso,
            WinProbability = PctOf("Probability (%)", "% de Ganho", "% de ganho").ToString(Inv),
            CloseInPeriodProbability = PctOf("% de Sair no Mês", "% de sair no mês", "% de sair no mes").ToString(Inv),
            Notes = get(new[] { "Status Comments", "Observação", "Observacao", "Observações", "Observacoes" }),
            PostponeCount = "0",
            CreatedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            UpdatedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            UpdatedBy = "Importação",

            // ---- Colunas do Funil de Vendas HSA ----
            Stage = get(new[] { "Stage" }),
            CommercialSegment = get(new[] { "Commercial Segment" }),
            Process = get(new[] { "Process" }),
            Brand = get(new[] { "Brand" }),
            EndUserSite = get(new[] { "Parent Opportunity: End User Site", "End User Site" }),
            Chance = get(new[] { "Chance" }),
            CustomerRef = get(new[] { "Customer Ref#", "Customer Ref" }),
            IsInterCompany = get(new[] { "Is Inter Company" }),
            Description = get(new[] { "Description" }),
            StatusDescription = get(new[] { "Status Description" }),
            AmountRaw = get(new[] { "Amount" }),
            Setor = "NB",                 // origem: planilha de New Business
        };

        if (dateIso == "") r.Warnings.Add($"Oportunidade {Show(proposta)}: data (Close Date) inválida ou ausente.");
        if (netBrl <= 0) r.Warnings.Add($"Oportunidade {Show(proposta)}: valor (Amount converted) ausente ou zero.");

        r.Rows.Add(o);
    }

    // ---- Monta uma oportunidade de AFTERMARKET (novo CRM) ------------------
    // Segue o De-Para de Configurações. Converte a moeda de origem (col Moeda +
    // Valor) para R$ pela taxa da aba Controle; BRL mantém. GM inteiro → %.
    private void BuildAfmRow(Result r, Func<string[], string> get, Func<string[], double?> getNum,
        Func<string, double?>? rate, Action<string>? onCurrency)
    {
        double? Num(params string[] names) => getNum(names) ?? ParseNum(get(names));
        double PctOf(params string[] names) => PctField(getNum(names), get(names));

        var quote = get(new[] { "QuoteNumber", "Quote Number", "Opportunity Number" });
        var plant = get(new[] { "PlantName", "Parent Opportunity: End User Site" });
        var value = Num("Valor", "Amount", "Net Value", "Amount (converted)") ?? 0;

        // Linha totalmente vazia: ignora silenciosamente.
        if (quote == "" && plant == "" && value == 0) return;
        r.Read++;

        var country = Match(_cat.Countries, c => c.Name, c => c.Id, get(new[] { "Country", "Installation Location" }));
        var market = Match(_cat.Markets, m => m.Name, m => m.Id, get(new[] { "Industry", "Market" }));
        var submarket = Match(_cat.SubMarkets, s => s.Name, s => s.Id, get(new[] { "SubIndustry", "Sub-Market" }));
        var product = Match(_cat.Products, p => p.Name, p => p.Id, get(new[] { "ProductType", "Product Type" }));
        var puv = MatchBu(get(new[] { "BU", "Business Unit" }));

        // Moeda (col M) + Valor (col N) → R$. BRL mantém; demais usam a taxa da
        // aba Controle. Sem taxa cadastrada → valor zerado + aviso.
        var cur = NormCurrency(get(new[] { "Moeda", "Currency", "Moeda da Proposta" }));
        double taxa;
        if (cur == "BRL") { taxa = 1; }
        else
        {
            onCurrency?.Invoke(cur);
            taxa = rate?.Invoke(cur) ?? 0;
            if (taxa <= 0)
                r.Warnings.Add($"Oportunidade {Show(quote)}: moeda {cur} sem taxa cadastrada na aba Controle — valor ficará zerado até cadastrar.");
        }

        var dateIso = ResolveDate(get(new[] { "PO Esperado", "Close Date", "Data" }), "");

        // Etapa do funil: converte o FunnelStage do AFM para o nome equivalente do
        // nosso funil (NB). Se o Status estiver marcado OTP, entra em E1 Order Entry.
        var status = get(new[] { "Status", "Status Description" });
        var stage = MapAfmStage(get(new[] { "FunnelStage", "Stage" }), status);

        // Id-base pela QuoteNumber (namespace "afm-" separado do New Business).
        var baseId = quote != "" ? "afm-" + Norm(quote) : "afm-" + Guid.NewGuid().ToString("N");
        var seq = r.IdSeq.TryGetValue(baseId, out var nseq) ? nseq + 1 : 1;
        r.IdSeq[baseId] = seq;
        var id = seq == 1 ? baseId : baseId + "-" + seq.ToString(Inv);

        var descr = get(new[] { "Description" });
        var o = new Opportunity
        {
            Id = id,
            Name = descr != "" ? descr : (quote != "" ? quote : "Oportunidade Aftermarket"),
            ProposalNumber = quote,
            CountryId = country,
            MarketId = market,
            SubMarketId = submarket,
            ProductId = product,
            CommercialCategory = "AFM",         // esta base é sempre Aftermarket
            PvBusinessUnitId = puv,
            CurrencyCode = cur,                  // moeda de origem preservada
            AmountOriginal = value.ToString(Inv),
            ExchangeRate = taxa.ToString(Inv),   // AmountBrl = valor × taxa (0 se sem taxa)
            GmPercent = PctOf("GM", "Gross Margin(%)", "Gross Margin (%)").ToString(Inv),
            ForecastCategory = "Pipeline",
            PipelineStageId = "st-qual",
            ExpectedDate = dateIso,
            CreatedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            UpdatedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            UpdatedBy = "Importação AFM",

            // ---- Colunas do Funil (via De-Para do Aftermarket) ----
            Stage = stage,                // FunnelStage AFM → etapa equivalente do funil
            CommercialSegment = get(new[] { "CRS_Market" }),
            Process = get(new[] { "Process" }),
            EndUserSite = plant,
            Chance = get(new[] { "Chance" }),
            CustomerRef = get(new[] { "ClientRef", "Customer Ref#" }),
            Description = descr,
            StatusDescription = status,
            AmountRaw = cur + " " + value.ToString(Inv),
            Setor = "AFM",                // origem: planilha de Aftermarket
        };

        if (dateIso == "") r.Warnings.Add($"Oportunidade {Show(quote)}: data (PO Esperado) inválida ou ausente.");
        if (value <= 0) r.Warnings.Add($"Oportunidade {Show(quote)}: valor (col Valor) ausente ou zero.");

        r.Rows.Add(o);
    }

    // Converte a etapa do funil do AFM para o nome equivalente do nosso funil (NB).
    // Regra especial: Status marcado OTP entra direto em "E1 Order Entry".
    private static string MapAfmStage(string afmStage, string status)
    {
        if (Norm(status).Split(' ').Contains("otp")) return "E1 Order Entry";
        return Norm(afmStage) switch
        {
            "qualification" => "Qualification",
            "value proposal" => "Arrived at Agreement on Need",
            "quote submission" => "Proposal Pending/Waiting",
            "quote clarification" => "Negotiation Phase/Review",
            "closing" => "Verbal Commitment",
            "" => "",
            _ => afmStage.Trim(),   // etapa desconhecida: preserva o texto original
        };
    }

    // Normaliza o código da moeda: símbolos e nomes → ISO (USD, EUR, GBP, BRL).
    private static string NormCurrency(string raw)
    {
        var n = Norm(raw).Replace("$", "").Replace(" ", "");
        return n switch
        {
            "" or "brl" or "r" or "real" or "reais" => "BRL",
            "usd" or "us" or "dolar" or "dollar" or "dolares" or "dollars" => "USD",
            "eur" or "euro" or "euros" => "EUR",
            "gbp" or "libra" or "libras" or "pound" or "pounds" => "GBP",
            _ => n.ToUpperInvariant(),
        };
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
        s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s == "") return null;

        // Decide o separador decimal pelo símbolo que aparece por ÚLTIMO — robusto
        // p/ pt-BR e en: "1.234.567,89", "1,234,567.89", "1234,56", "1.234.567".
        int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
        if (dot >= 0 && comma >= 0)
            s = comma > dot ? s.Replace(".", "").Replace(',', '.')
                            : s.Replace(",", "");
        else if (comma >= 0)
            s = (s.IndexOf(',') == comma && s.Length - comma - 1 <= 2)
                ? s.Replace(',', '.')
                : s.Replace(",", "");
        else if (dot >= 0 && s.Count(c => c == '.') > 1)
            s = s.Replace(".", "");

        return double.TryParse(s, NumberStyles.Any, Inv, out var v) ? v : (double?)null;
    }

    // Percentual: usa o valor numérico da célula (0,94 → 94; 94 → 94) ou, na
    // falta, interpreta o texto preservando a semântica do "%".
    private static double PctField(double? num, string text)
    {
        if (num is { } n) return Math.Clamp(n > 0 && n <= 1 ? n * 100 : n, 0, 100);
        return Pct(text);
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
