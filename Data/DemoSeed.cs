using System.Globalization;
using HowdenSalesForecast.Models;

namespace HowdenSalesForecast.Data;

// ---------------------------------------------------------------------------
// Gera dados demonstrativos realistas (Howden Brasil): 40 oportunidades
// distribuídas por categoria, KAM, mercado, produto, cliente/planta e moeda.
// Determinístico (semente fixa) para uma demonstração estável.
// ---------------------------------------------------------------------------
public static class DemoSeed
{
    private static readonly string[] Acoes =
    {
        "Enviar proposta revisada", "Follow-up técnico com engenharia", "Reunião de negociação",
        "Cobrar ordem de compra", "Visita à planta", "Alinhar escopo com cliente",
        "Apresentar caso de retorno (payback)", "Confirmar budget do cliente",
    };
    private static readonly string[] Riscos =
    {
        "Concorrência da Atlas em preço", "Aprovação de CAPEX pendente no cliente",
        "Prazo de entrega apertado", "Dependência de parada programada da planta",
        "Câmbio pressiona a margem", "", "Especificação técnica em definição", "",
    };

    private static readonly Dictionary<ForecastCategory, string[]> StageByCat = new()
    {
        [ForecastCategory.Commit] = new[] { "st-po", "st-appr" },
        [ForecastCategory.BestCase] = new[] { "st-neg", "st-sent" },
        [ForecastCategory.Pipeline] = new[] { "st-qual", "st-tech", "st-prep" },
        [ForecastCategory.Upside] = new[] { "st-id", "st-qual" },
        [ForecastCategory.Risk] = new[] { "st-sent", "st-neg" },
        [ForecastCategory.ClosedWon] = new[] { "st-won" },
        [ForecastCategory.ClosedLost] = new[] { "st-lost" },
        [ForecastCategory.Postponed] = new[] { "st-neg" },
    };

    public static List<Opportunity> Opportunities(Catalog cat)
    {
        var rnd = new Random(42);
        var today = DateTime.Today;

        var plantsByCustomer = cat.Plants.GroupBy(p => p.CustomerId).ToDictionary(g => g.Key, g => g.ToArray());
        var subByMarket = cat.SubMarkets.GroupBy(s => s.MarketId).ToDictionary(g => g.Key, g => g.ToArray());

        var plan = new List<ForecastCategory>();
        void Add(ForecastCategory c, int n) { for (var i = 0; i < n; i++) plan.Add(c); }
        Add(ForecastCategory.Commit, 8);
        Add(ForecastCategory.BestCase, 8);
        Add(ForecastCategory.Pipeline, 10);
        Add(ForecastCategory.Upside, 4);
        Add(ForecastCategory.Risk, 4);
        Add(ForecastCategory.ClosedWon, 3);
        Add(ForecastCategory.ClosedLost, 2);
        Add(ForecastCategory.Postponed, 1);

        var list = new List<Opportunity>();
        for (var i = 0; i < plan.Count; i++)
            list.Add(Build(cat, plan[i], i, rnd, today, plantsByCustomer, subByMarket));
        return list;
    }

    private static Opportunity Build(Catalog cat, ForecastCategory fc, int i, Random rnd, DateTime today,
        Dictionary<string, Plant[]> plantsByCustomer, Dictionary<string, SubMarket[]> subByMarket)
    {
        string Iso(DateTime d) => d.ToString("yyyy-MM-dd");
        string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
        T Pick<T>(IReadOnlyList<T> a) => a[rnd.Next(a.Count)];

        var customer = Pick(cat.Customers);
        var plant = plantsByCustomer.TryGetValue(customer.Id, out var ps) && ps.Length > 0
            ? ps[rnd.Next(ps.Length)] : cat.Plants[0];
        var marketId = customer.MarketId;
        var sub = subByMarket.TryGetValue(marketId, out var sm) && sm.Length > 0 ? sm[rnd.Next(sm.Length)] : null;
        var product = Pick(cat.Products);
        var kam = Pick(cat.Kams);

        // Probabilidades e prazos por categoria.
        (int win, int close, int dMin, int dMax) = fc switch
        {
            ForecastCategory.Commit => (rnd.Next(85, 96), rnd.Next(80, 96), 0, 55),
            ForecastCategory.BestCase => (rnd.Next(55, 76), rnd.Next(50, 71), 15, 90),
            ForecastCategory.Pipeline => (rnd.Next(20, 41), rnd.Next(20, 46), 60, 180),
            ForecastCategory.Upside => (rnd.Next(15, 31), rnd.Next(20, 41), 90, 240),
            ForecastCategory.Risk => (rnd.Next(40, 61), rnd.Next(40, 66), -12, 45),
            ForecastCategory.ClosedWon => (100, 100, -60, -1),
            ForecastCategory.ClosedLost => (0, 0, -90, -10),
            _ => (rnd.Next(30, 51), rnd.Next(10, 31), 120, 210), // Postponed
        };

        var expected = today.AddDays(rnd.Next(dMin, dMax + 1));

        // Valor: industrial, US$ 50k–3M. Parte em BRL, parte em USD, um pouco em EUR.
        var baseUsd = product.Family switch
        {
            "Aftermarket" => rnd.Next(50, 400) * 1000.0,
            "Serviços" => rnd.Next(40, 250) * 1000.0,
            "Compressores" => rnd.Next(400, 3000) * 1000.0,
            _ => rnd.Next(150, 1600) * 1000.0,
        };

        // Demonstração em BRL e USD (a taxa é sempre a cotação BRL/USD).
        var currency = i % 3 == 0 ? "USD" : "BRL";
        var brlPerUsd = cat.RateToUsd("USD");
        // Arredonda ao milhar (Math.Round não aceita dígitos negativos).
        double ToThousand(double v) => Math.Round(v / 1000.0) * 1000.0;
        var amountOriginal = currency == "USD"
            ? Inv(ToThousand(baseUsd))
            : Inv(ToThousand(baseUsd * brlPerUsd));
        var exchange = Inv(brlPerUsd);

        var gm = rnd.Next(16, 43);
        var postpones = fc switch
        {
            ForecastCategory.Postponed => rnd.Next(2, 4),
            ForecastCategory.Risk => rnd.Next(1, 3),
            _ => rnd.Next(0, 2),
        };
        var staleDays = fc is ForecastCategory.Risk or ForecastCategory.Upside
            ? rnd.Next(20, 75) : rnd.Next(0, 18);
        var updated = today.AddDays(-staleDays);

        // Risk/Upside às vezes sem próxima ação (dispara score de risco).
        var semAcao = fc is ForecastCategory.Risk or ForecastCategory.Upside ? rnd.Next(0, 2) == 0 : false;
        var acao = semAcao ? "" : Acoes[rnd.Next(Acoes.Length)];
        var acaoDate = semAcao ? "" : Iso(today.AddDays(rnd.Next(-5, 15)));

        // Previsão do gestor: às vezes diverge (mais conservadora).
        string managerProb = "";
        if (fc is ForecastCategory.Commit or ForecastCategory.BestCase or ForecastCategory.Risk && rnd.Next(0, 2) == 0)
            managerProb = Inv(Math.Clamp(win - rnd.Next(5, 25), 0, 100));

        var stage = StageByCat[fc][rnd.Next(StageByCat[fc].Length)];
        var category = product.Family switch
        {
            "Aftermarket" => "AFM",
            "Serviços" => "SV",
            _ => rnd.Next(0, 2) == 0 ? "NB" : "RT",
        };
        var pvBu = Pick(cat.BusinessUnits).Id;

        // ~1 em cada 4 é negócio de exportação — o país difere do cliente (BR).
        var exportCountries = cat.Countries.Where(c => c.Id != "br").ToList();
        var country = rnd.Next(0, 4) == 0 && exportCountries.Count > 0
            ? Pick(exportCountries).Id
            : customer.CountryId;

        var n = 1001 + i;
        return new Opportunity
        {
            Id = $"opp-{n}",
            Name = $"{product.Name} — {plant.Name}",
            ProposalNumber = $"PRP-2026-{n}",
            PvNumber = $"PV-{2600 + i}",
            CountryId = country,
            MarketId = marketId,
            SubMarketId = sub?.Id ?? "",
            ProductId = product.Id,
            EquipmentTypeId = EquipForProduct(product),
            KamId = kam.Id,
            CustomerId = customer.Id,
            PlantId = plant.Id,
            CommercialCategory = category,
            IntercompanyBu = rnd.Next(0, 4) == 0 ? "bu-hg" : "",
            PvBusinessUnitId = pvBu,
            CurrencyCode = currency,
            AmountOriginal = amountOriginal,
            ExchangeRate = exchange,
            GmPercent = Inv(gm),
            ForecastCategory = fc.ToString(),
            PipelineStageId = stage,
            ExpectedDate = Iso(expected),
            WinProbability = Inv(win),
            CloseInPeriodProbability = Inv(close),
            ManagerProbability = managerProb,
            Justification = fc == ForecastCategory.Commit ? "PO em emissão pelo cliente." : "",
            NextAction = acao,
            NextActionDate = acaoDate,
            Risks = Riscos[rnd.Next(Riscos.Length)],
            Notes = "",
            PostponeCount = Inv(postpones),
            CreatedAt = Iso(today.AddDays(-rnd.Next(30, 200))),
            UpdatedAt = Iso(updated),
            UpdatedBy = kam.Name,
            ValueChangedAt = rnd.Next(0, 3) == 0 ? Iso(today.AddDays(-rnd.Next(0, 10))) : "",
            DateChangedAt = fc == ForecastCategory.Postponed ? Iso(today.AddDays(-rnd.Next(0, 14))) : "",
        };
    }

    private static string EquipForProduct(Product p) => p.Family switch
    {
        "Ventiladores" => "eq-fan",
        "Compressores" => "eq-comp",
        "Sopradores" => "eq-blw",
        "Recuperação de Calor" => "eq-hx",
        "Aftermarket" => "eq-parts",
        _ => "eq-svc",
    };
}
