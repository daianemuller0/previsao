using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Catálogo de dados-mestre (Howden Brasil). Determinístico, injetado como
// singleton. Fornece as listas de referência e dicionários de lookup por Id.
// Em produção, esta camada seria substituída por repositórios/serviços que
// leem do banco corporativo — a UI continua consumindo a mesma interface.
// ---------------------------------------------------------------------------
public sealed class Catalog
{
    public IReadOnlyList<Country> Countries { get; }
    public IReadOnlyList<Market> Markets { get; }
    public IReadOnlyList<SubMarket> SubMarkets { get; }
    public IReadOnlyList<Product> Products { get; }
    public IReadOnlyList<EquipmentType> EquipmentTypes { get; }
    public IReadOnlyList<BusinessUnit> BusinessUnits { get; }
    public IReadOnlyList<Kam> Kams { get; }
    public IReadOnlyList<Customer> Customers { get; }
    public IReadOnlyList<Plant> Plants { get; }
    public IReadOnlyList<Currency> Currencies { get; }
    public IReadOnlyList<ExchangeRate> ExchangeRates { get; }
    public IReadOnlyList<PipelineStage> Stages { get; }
    public IReadOnlyList<SalesTarget> Targets { get; }

    private readonly Dictionary<string, Country> _country;
    private readonly Dictionary<string, Market> _market;
    private readonly Dictionary<string, SubMarket> _submarket;
    private readonly Dictionary<string, Product> _product;
    private readonly Dictionary<string, EquipmentType> _equip;
    private readonly Dictionary<string, BusinessUnit> _bu;
    private readonly Dictionary<string, Kam> _kam;
    private readonly Dictionary<string, Customer> _customer;
    private readonly Dictionary<string, Plant> _plant;
    private readonly Dictionary<string, PipelineStage> _stage;
    private readonly Dictionary<string, double> _rate;

    public Catalog()
    {
        Countries = new[]
        {
            new Country("br", "Brasil", "BR"),
            new Country("cl", "Chile", "CL"),
            new Country("pe", "Peru", "PE"),
            new Country("ar", "Argentina", "AR"),
        };

        Markets = new[]
        {
            new Market("mkt-cem", "Cimento"),
            new Market("mkt-min", "Mineração"),
            new Market("mkt-pwr", "Energia"),
            new Market("mkt-stl", "Siderurgia"),
            new Market("mkt-pnp", "Papel e Celulose"),
        };

        SubMarkets = new[]
        {
            new SubMarket("sm-cem-clk", "mkt-cem", "Clínquer"),
            new SubMarket("sm-cem-grn", "mkt-cem", "Moagem"),
            new SubMarket("sm-min-fe", "mkt-min", "Minério de Ferro"),
            new SubMarket("sm-min-cu", "mkt-min", "Cobre"),
            new SubMarket("sm-pwr-tpp", "mkt-pwr", "Termelétrica"),
            new SubMarket("sm-pwr-bio", "mkt-pwr", "Biomassa"),
            new SubMarket("sm-stl-blf", "mkt-stl", "Alto-forno"),
            new SubMarket("sm-pnp-krf", "mkt-pnp", "Kraft"),
        };

        Products = new[]
        {
            new Product("prd-cf", "Ventilador Centrífugo", "Ventiladores"),
            new Product("prd-af", "Ventilador Axial", "Ventiladores"),
            new Product("prd-api", "Ventilador API 673", "Ventiladores"),
            new Product("prd-rhe", "Trocador de Calor Rotativo", "Recuperação de Calor"),
            new Product("prd-sc", "Compressor de Parafuso", "Compressores"),
            new Product("prd-cc", "Compressor Centrífugo", "Compressores"),
            new Product("prd-dc", "Compressor de Diafragma", "Compressores"),
            new Product("prd-rb", "Soprador Roots", "Sopradores"),
            new Product("prd-parts", "Peças e Componentes", "Aftermarket"),
            new Product("prd-svc", "Serviços de Campo", "Serviços"),
        };

        EquipmentTypes = new[]
        {
            new EquipmentType("eq-fan", "Ventilador industrial"),
            new EquipmentType("eq-comp", "Compressor"),
            new EquipmentType("eq-blw", "Soprador"),
            new EquipmentType("eq-hx", "Trocador de calor"),
            new EquipmentType("eq-parts", "Kit de peças"),
            new EquipmentType("eq-svc", "Serviço"),
        };

        BusinessUnits = new[]
        {
            new BusinessUnit("bu-hsa", "HSA", "Howden São Paulo (Brasil)"),
            new BusinessUnit("bu-hbr", "HBR", "Howden do Brasil"),
            new BusinessUnit("bu-hcl", "HCL", "Howden Chile"),
            new BusinessUnit("bu-hpe", "HPE", "Howden Peru"),
            new BusinessUnit("bu-hg", "HG", "Howden Global"),
        };

        Kams = new[]
        {
            new Kam("kam-rmc", "Ricardo Mendes", "ricardo.mendes@howden.com", "Sudeste"),
            new Kam("kam-abr", "Ana Beatriz Rocha", "ana.rocha@howden.com", "Sul"),
            new Kam("kam-lfs", "Luís Fernando Souza", "luis.souza@howden.com", "Norte/Nordeste"),
            new Kam("kam-cmp", "Camila Prado", "camila.prado@howden.com", "Centro-Oeste"),
            new Kam("kam-tgn", "Thiago Nogueira", "thiago.nogueira@howden.com", "Exportação"),
        };

        Customers = new[]
        {
            new Customer("cus-vot", "Votorantim Cimentos", "br", "mkt-cem"),
            new Customer("cus-vale", "Vale", "br", "mkt-min"),
            new Customer("cus-csn", "CSN", "br", "mkt-stl"),
            new Customer("cus-ger", "Gerdau", "br", "mkt-stl"),
            new Customer("cus-suz", "Suzano", "br", "mkt-pnp"),
            new Customer("cus-eld", "Eldorado Brasil", "br", "mkt-pnp"),
            new Customer("cus-cba", "CBA — Cia. Brasileira de Alumínio", "br", "mkt-min"),
            new Customer("cus-eng", "Engie Brasil Energia", "br", "mkt-pwr"),
        };

        Plants = new[]
        {
            new Plant("pl-vot-rio", "cus-vot", "Fábrica Rio Branco", "br", "Rio Branco do Sul/PR"),
            new Plant("pl-vot-cub", "cus-vot", "Fábrica Cubatão", "br", "Cubatão/SP"),
            new Plant("pl-vale-car", "cus-vale", "Complexo Carajás", "br", "Parauapebas/PA"),
            new Plant("pl-vale-itb", "cus-vale", "Mina de Itabira", "br", "Itabira/MG"),
            new Plant("pl-csn-vre", "cus-csn", "Usina Presidente Vargas", "br", "Volta Redonda/RJ"),
            new Plant("pl-ger-oph", "cus-ger", "Usina Ouro Branco", "br", "Ouro Branco/MG"),
            new Plant("pl-ger-cha", "cus-ger", "Charqueadas", "br", "Charqueadas/RS"),
            new Plant("pl-suz-imp", "cus-suz", "Unidade Imperatriz", "br", "Imperatriz/MA"),
            new Plant("pl-suz-trs", "cus-suz", "Unidade Três Lagoas", "br", "Três Lagoas/MS"),
            new Plant("pl-eld-trs", "cus-eld", "Planta Três Lagoas", "br", "Três Lagoas/MS"),
            new Plant("pl-cba-alu", "cus-cba", "Unidade Alumínio", "br", "Alumínio/SP"),
            new Plant("pl-eng-jor", "cus-eng", "UTE Jorge Lacerda", "br", "Capivari de Baixo/SC"),
        };

        Currencies = new[]
        {
            new Currency("BRL", "R$", "Real"),
            new Currency("USD", "US$", "Dólar americano"),
            new Currency("EUR", "€", "Euro"),
        };

        ExchangeRates = new[]
        {
            new ExchangeRate("BRL", 1.0),   // referência
            new ExchangeRate("USD", 5.42),  // BRL por USD
            new ExchangeRate("EUR", 5.88),  // BRL por EUR
        };

        Stages = new[]
        {
            new PipelineStage("st-id", 1, "Identificação", 10),
            new PipelineStage("st-qual", 2, "Qualificação", 20),
            new PipelineStage("st-tech", 3, "Levantamento técnico", 35),
            new PipelineStage("st-prep", 4, "Preparação da proposta", 45),
            new PipelineStage("st-sent", 5, "Proposta enviada", 60),
            new PipelineStage("st-neg", 6, "Negociação", 75),
            new PipelineStage("st-appr", 7, "Aprovação do cliente", 85),
            new PipelineStage("st-po", 8, "Ordem esperada", 92),
            new PipelineStage("st-won", 9, "Closed Won", 100),
            new PipelineStage("st-lost", 10, "Closed Lost", 0),
        };

        // Metas mensais (USD) do ano corrente: geral + rateio por KAM.
        var year = DateTime.Today.Year;
        var mensalGeral = new[] { 2200, 2400, 2600, 2500, 2700, 3000, 2800, 2900, 3100, 3000, 3200, 3500 };
        var kamShare = new[] { 0.26, 0.22, 0.20, 0.18, 0.14 }; // soma 1.0
        var targets = new List<SalesTarget>();
        for (var m = 1; m <= 12; m++)
        {
            var geral = mensalGeral[m - 1] * 1000.0;
            targets.Add(new SalesTarget($"tgt-all-{year}-{m}", year, m, "all", "", geral));
            for (var k = 0; k < Kams.Count; k++)
                targets.Add(new SalesTarget($"tgt-{Kams[k].Id}-{year}-{m}", year, m, "kam", Kams[k].Id, geral * kamShare[k]));
        }
        Targets = targets;

        _country = Countries.ToDictionary(x => x.Id);
        _market = Markets.ToDictionary(x => x.Id);
        _submarket = SubMarkets.ToDictionary(x => x.Id);
        _product = Products.ToDictionary(x => x.Id);
        _equip = EquipmentTypes.ToDictionary(x => x.Id);
        _bu = BusinessUnits.ToDictionary(x => x.Id);
        _kam = Kams.ToDictionary(x => x.Id);
        _customer = Customers.ToDictionary(x => x.Id);
        _plant = Plants.ToDictionary(x => x.Id);
        _stage = Stages.ToDictionary(x => x.Id);
        _rate = ExchangeRates.ToDictionary(x => x.CurrencyCode, x => x.RateToUsd);
        // Mantém a taxa BRL/USD global (usada no cálculo do Valor em USD) em sincronia.
        if (_rate.TryGetValue("USD", out var usd) && usd > 0)
            HowdenSalesForecast.Models.Opportunity.BrlPerUsd = usd;
    }

    // ---- lookups (nome por Id) ---------------------------------------------
    // Id conhecido → nome do catálogo. Id vazio → "—". Caso contrário, devolve o
    // próprio valor: assim dados importados (que guardam o texto quando o nome
    // não casa com o catálogo) continuam aparecendo na tela em vez de "—".
    private static string Raw(string id) => string.IsNullOrWhiteSpace(id) ? "—" : id;
    public string CountryName(string id) => _country.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string MarketName(string id) => _market.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string SubMarketName(string id) => _submarket.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string ProductName(string id) => _product.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string EquipmentName(string id) => _equip.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string BuCode(string id) => _bu.TryGetValue(id, out var v) ? v.Code : Raw(id);
    public string BuName(string id) => _bu.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string KamName(string id) => _kam.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string CustomerName(string id) => _customer.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string PlantName(string id) => _plant.TryGetValue(id, out var v) ? v.Name : Raw(id);
    public string StageName(string id) => _stage.TryGetValue(id, out var v) ? v.Name : "—";
    public int StageOrder(string id) => _stage.TryGetValue(id, out var v) ? v.Order : 0;

    public Kam? GetKam(string id) => _kam.GetValueOrDefault(id);
    public Customer? GetCustomer(string id) => _customer.GetValueOrDefault(id);
    public Plant? GetPlant(string id) => _plant.GetValueOrDefault(id);
    public double RateToUsd(string currency) => _rate.TryGetValue(currency, out var r) ? r : 1.0;

    // ---- metas (USD) -------------------------------------------------------
    public double TargetMonth(int year, int month, string scope = "all", string scopeId = "") =>
        Targets.Where(t => t.Year == year && t.Month == month && t.Scope == scope && t.ScopeId == scopeId)
               .Sum(t => t.AmountUsd);

    public double TargetQuarter(int year, int quarter, string scope = "all", string scopeId = "") =>
        Targets.Where(t => t.Year == year && (t.Month - 1) / 3 + 1 == quarter && t.Scope == scope && t.ScopeId == scopeId)
               .Sum(t => t.AmountUsd);

    public double TargetYear(int year, string scope = "all", string scopeId = "") =>
        Targets.Where(t => t.Year == year && t.Scope == scope && t.ScopeId == scopeId).Sum(t => t.AmountUsd);
}
