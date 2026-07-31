namespace HowdenSalesForecast.Models;

// ---------------------------------------------------------------------------
// Entidades de dados-mestre (cadastros). Em produção viriam do banco/API
// corporativa; aqui compõem um catálogo determinístico para a demonstração.
// A oportunidade referencia essas entidades por Id (evita texto livre).
// ---------------------------------------------------------------------------

public record Country(string Id, string Name, string Iso);

public record Market(string Id, string Name);

public record SubMarket(string Id, string MarketId, string Name);

public record Product(string Id, string Name, string Family);

public record EquipmentType(string Id, string Name);

public record BusinessUnit(string Id, string Code, string Name);

public record Kam(string Id, string Name, string Email, string Region);

public record Customer(string Id, string Name, string CountryId, string MarketId);

public record Plant(string Id, string CustomerId, string Name, string CountryId, string Location);

public record Currency(string Code, string Symbol, string Name);

// Taxa de câmbio para USD (1 unidade da moeda = RateToUsd USD).
public record ExchangeRate(string CurrencyCode, double RateToUsd);

public record PipelineStage(string Id, int Order, string Name, double DefaultProbability);

// Meta comercial por período e escopo (geral, por KAM ou por mercado). Em USD.
public record SalesTarget(string Id, int Year, int Month, string Scope, string ScopeId, double AmountUsd);

// Categoria comercial da oportunidade (New Business / Retrofit / Aftermarket / Service).
public static class CommercialCategory
{
    public const string NewBusiness = "NB";
    public const string Retrofit = "RT";
    public const string Aftermarket = "AFM";
    public const string Service = "SV";

    public static readonly (string Code, string Name)[] All =
    {
        (NewBusiness, "New Business"),
        (Retrofit, "Retrofit"),
        (Aftermarket, "Aftermarket"),
        (Service, "Service"),
    };

    public static string Name(string code) => All.FirstOrDefault(c => c.Code == code).Name ?? code;
}
